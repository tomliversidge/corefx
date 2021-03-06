// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// =+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
//
// HashJoinQueryOperatorEnumerator.cs
//
// =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-

using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Threading;

namespace System.Linq.Parallel
{
    /// <summary>
    /// This enumerator implements the hash-join algorithm as noted earlier.
    ///
    /// Assumptions:
    ///     This enumerator type won't work properly at all if the analysis engine didn't
    ///     ensure a proper hash-partition. We expect inner and outer elements with equal
    ///     keys are ALWAYS in the same partition. If they aren't (e.g. if the analysis is
    ///     busted) we'll silently drop items on the floor. :( 
    ///     
    ///     
    ///  This is the enumerator class for two operators:
    ///   - Join
    ///   - GroupJoin
    /// </summary>
    /// <typeparam name="TLeftInput"></typeparam>
    /// <typeparam name="TLeftKey"></typeparam>
    /// <typeparam name="TRightInput"></typeparam>
    /// <typeparam name="THashKey"></typeparam>
    /// <typeparam name="TOutput"></typeparam>
    internal class HashJoinQueryOperatorEnumerator<TLeftInput, TLeftKey, TRightInput, THashKey, TOutput>
        : QueryOperatorEnumerator<TOutput, TLeftKey>
    {
        private readonly QueryOperatorEnumerator<Pair, TLeftKey> _leftSource; // Left (outer) data source. For probing.
        private readonly QueryOperatorEnumerator<Pair, int> _rightSource; // Right (inner) data source. For building.
        private readonly Func<TLeftInput, TRightInput, TOutput> _singleResultSelector; // Single result selector.
        private readonly Func<TLeftInput, IEnumerable<TRightInput>, TOutput> _groupResultSelector; // Group result selector.
        private readonly IEqualityComparer<THashKey> _keyComparer; // An optional key comparison object.
        private readonly CancellationToken _cancellationToken;
        private Mutables _mutables;

        private class Mutables
        {
            internal TLeftInput _currentLeft; // The current matching left element.
            internal TLeftKey _currentLeftKey; // The current index of the matching left element.
            internal HashLookup<THashKey, Pair> _rightHashLookup; // The hash lookup.
            internal ListChunk<TRightInput> _currentRightMatches; // Current right matches (if any).
            internal int _currentRightMatchesIndex; // Current index in the set of right matches.
            internal int _outputLoopCount;
        }

        //---------------------------------------------------------------------------------------
        // Instantiates a new hash-join enumerator.
        //

        internal HashJoinQueryOperatorEnumerator(
            QueryOperatorEnumerator<Pair, TLeftKey> leftSource,
            QueryOperatorEnumerator<Pair, int> rightSource,
            Func<TLeftInput, TRightInput, TOutput> singleResultSelector,
            Func<TLeftInput, IEnumerable<TRightInput>, TOutput> groupResultSelector,
            IEqualityComparer<THashKey> keyComparer,
            CancellationToken cancellationToken)
        {
            Contract.Assert(leftSource != null);
            Contract.Assert(rightSource != null);
            Contract.Assert(singleResultSelector != null || groupResultSelector != null);

            _leftSource = leftSource;
            _rightSource = rightSource;
            _singleResultSelector = singleResultSelector;
            _groupResultSelector = groupResultSelector;
            _keyComparer = keyComparer;
            _cancellationToken = cancellationToken;
        }

        //---------------------------------------------------------------------------------------
        // MoveNext implements all the hash-join logic noted earlier. When it is called first, it
        // will execute the entire inner query tree, and build a hash-table lookup. This is the
        // Building phase. Then for the first call and all subsequent calls to MoveNext, we will
        // incrementally perform the Probing phase. We'll keep getting elements from the outer
        // data source, looking into the hash-table we built, and enumerating the full results.
        //
        // This routine supports both inner and outer (group) joins. An outer join will yield a
        // (possibly empty) list of matching elements from the inner instead of one-at-a-time,
        // as we do for inner joins.
        //

        internal override bool MoveNext(ref TOutput currentElement, ref TLeftKey currentKey)
        {
            Contract.Assert(_singleResultSelector != null || _groupResultSelector != null, "expected a compiled result selector");
            Contract.Assert(_leftSource != null);
            Contract.Assert(_rightSource != null);

            // BUILD phase: If we haven't built the hash-table yet, create that first.
            Mutables mutables = _mutables;
            if (mutables == null)
            {
                mutables = _mutables = new Mutables();
#if DEBUG
                int hashLookupCount = 0;
                int hashKeyCollisions = 0;
#endif
                mutables._rightHashLookup = new HashLookup<THashKey, Pair>(_keyComparer);

                Pair rightPair = new Pair(default(TRightInput), default(THashKey));
                int rightKeyUnused = default(int);
                int i = 0;
                while (_rightSource.MoveNext(ref rightPair, ref rightKeyUnused))
                {
                    if ((i++ & CancellationState.POLL_INTERVAL) == 0)
                        CancellationState.ThrowIfCanceled(_cancellationToken);

                    TRightInput rightElement = (TRightInput)rightPair.First;
                    THashKey rightHashKey = (THashKey)rightPair.Second;

                    // We ignore null keys.
                    if (rightHashKey != null)
                    {
#if DEBUG
                        hashLookupCount++;
#endif

                        // See if we've already stored an element under the current key. If not, we
                        // lazily allocate a pair to hold the elements mapping to the same key.
                        const int INITIAL_CHUNK_SIZE = 2;
                        Pair currentValue = new Pair(default(TRightInput), default(ListChunk<TRightInput>));
                        if (!mutables._rightHashLookup.TryGetValue(rightHashKey, ref currentValue))
                        {
                            currentValue = new Pair(rightElement, null);

                            if (_groupResultSelector != null)
                            {
                                // For group joins, we also add the element to the list. This makes
                                // it easier later to yield the list as-is.
                                currentValue.Second = new ListChunk<TRightInput>(INITIAL_CHUNK_SIZE);
                                ((ListChunk<TRightInput>)currentValue.Second).Add((TRightInput)rightElement);
                            }

                            mutables._rightHashLookup.Add(rightHashKey, currentValue);
                        }
                        else
                        {
                            if (currentValue.Second == null)
                            {
                                // Lazily allocate a list to hold all but the 1st value. We need to
                                // re-store this element because the pair is a value type.
                                currentValue.Second = new ListChunk<TRightInput>(INITIAL_CHUNK_SIZE);
                                mutables._rightHashLookup[rightHashKey] = currentValue;
                            }

                            ((ListChunk<TRightInput>)currentValue.Second).Add((TRightInput)rightElement);
#if DEBUG
                            hashKeyCollisions++;
#endif
                        }
                    }
                }

#if DEBUG
                TraceHelpers.TraceInfo("ParallelJoinQueryOperator::MoveNext - built hash table [count = {0}, collisions = {1}]",
                    hashLookupCount, hashKeyCollisions);
#endif
            }

            // PROBE phase: So long as the source has a next element, return the match.
            ListChunk<TRightInput> currentRightChunk = mutables._currentRightMatches;
            if (currentRightChunk != null && mutables._currentRightMatchesIndex == currentRightChunk.Count)
            {
                currentRightChunk = mutables._currentRightMatches = currentRightChunk.Next;
                mutables._currentRightMatchesIndex = 0;
            }

            if (mutables._currentRightMatches == null)
            {
                // We have to look up the next list of matches in the hash-table.
                Pair leftPair = new Pair(default(TLeftInput), default(THashKey));
                TLeftKey leftKey = default(TLeftKey);
                while (_leftSource.MoveNext(ref leftPair, ref leftKey))
                {
                    if ((mutables._outputLoopCount++ & CancellationState.POLL_INTERVAL) == 0)
                        CancellationState.ThrowIfCanceled(_cancellationToken);

                    // Find the match in the hash table.
                    Pair matchValue = new Pair(default(TRightInput), default(ListChunk<TRightInput>));
                    TLeftInput leftElement = (TLeftInput)leftPair.First;
                    THashKey leftHashKey = (THashKey)leftPair.Second;

                    // Ignore null keys.
                    if (leftHashKey != null)
                    {
                        if (mutables._rightHashLookup.TryGetValue(leftHashKey, ref matchValue))
                        {
                            // We found a new match. For inner joins, we remember the list in case
                            // there are multiple value under this same key -- the next iteration will pick
                            // them up. For outer joins, we will use the list momentarily.
                            if (_singleResultSelector != null)
                            {
                                mutables._currentRightMatches = (ListChunk<TRightInput>)matchValue.Second;
                                Contract.Assert(mutables._currentRightMatches == null || mutables._currentRightMatches.Count > 0,
                                                "we were expecting that the list would be either null or empty");
                                mutables._currentRightMatchesIndex = 0;

                                // Yield the value.
                                currentElement = _singleResultSelector(leftElement, (TRightInput)matchValue.First);
                                currentKey = leftKey;

                                // If there is a list of matches, remember the left values for next time.
                                if (matchValue.Second != null)
                                {
                                    mutables._currentLeft = leftElement;
                                    mutables._currentLeftKey = leftKey;
                                }

                                return true;
                            }
                        }
                    }

                    // For outer joins, we always yield a result.
                    if (_groupResultSelector != null)
                    {
                        // Grab the matches, or create an empty list if there are none.
                        IEnumerable<TRightInput> matches = (ListChunk<TRightInput>)matchValue.Second;
                        if (matches == null)
                        {
                            matches = ParallelEnumerable.Empty<TRightInput>();
                        }

                        // Generate the current value.
                        currentElement = _groupResultSelector(leftElement, matches);
                        currentKey = leftKey;
                        return true;
                    }
                }

                // If we've reached the end of the data source, we're done.
                return false;
            }

            // Produce the next element and increment our index within the matches.
            Contract.Assert(_singleResultSelector != null);
            Contract.Assert(mutables._currentRightMatches != null);
            Contract.Assert(0 <= mutables._currentRightMatchesIndex && mutables._currentRightMatchesIndex < mutables._currentRightMatches.Count);

            currentElement = _singleResultSelector(
                mutables._currentLeft, mutables._currentRightMatches._chunk[mutables._currentRightMatchesIndex]);
            currentKey = mutables._currentLeftKey;

            mutables._currentRightMatchesIndex++;

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Contract.Assert(_leftSource != null && _rightSource != null);
            _leftSource.Dispose();
            _rightSource.Dispose();
        }
    }
}
