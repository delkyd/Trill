﻿// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using System;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.Serialization;
using Microsoft.StreamProcessing.Internal;
using Microsoft.StreamProcessing.Internal.Collections;

namespace Microsoft.StreamProcessing
{
    // Version of AlterLifetime with vother func as (a) => c
    [DataContract]
    internal sealed class PartitionedAlterLifetimeStartDependentDurationPipe<TKey, TPayload, TPartitionKey> : UnaryPipe<TKey, TPayload, TPayload>, IStreamObserver<TKey, TPayload>
    {
        private readonly MemoryPool<TKey, TPayload> pool;

        [SchemaSerialization]
        private readonly Expression<Func<long, long>> startTimeDurationSelector;
        private readonly Func<long, long> startTimeDurationSelectorCompiled;
        [SchemaSerialization]
        private readonly Expression<Func<long, long>> startTimeSelector;
        private readonly Func<long, long> startTimeSelectorCompiled;

        [DataMember]
        private FastDictionary<TPartitionKey, long> lastSync = new FastDictionary<TPartitionKey, long>();

        private readonly Func<TKey, TPartitionKey> getPartitionKey;

        [Obsolete("Used only by serialization. Do not call directly.")]
        public PartitionedAlterLifetimeStartDependentDurationPipe()
        {
            this.getPartitionKey = GetPartitionExtractor<TPartitionKey, TKey>();
        }

        public PartitionedAlterLifetimeStartDependentDurationPipe(AlterLifetimeStreamable<TKey, TPayload> stream, IStreamObserver<TKey, TPayload> observer)
            : base(stream, observer)
        {
            Contract.Requires(stream != null);

            this.startTimeDurationSelector = (Expression<Func<long, long>>)stream.DurationSelector;
            this.startTimeDurationSelectorCompiled = this.startTimeDurationSelector.Compile();
            this.startTimeSelector = (Expression<Func<long, long>>)stream.StartTimeSelector;
            this.startTimeSelectorCompiled = this.startTimeSelector.Compile();
            this.getPartitionKey = GetPartitionExtractor<TPartitionKey, TKey>();
            this.pool = MemoryManager.GetMemoryPool<TKey, TPayload>(stream.Properties.IsColumnar);
        }

        public override void ProduceQueryPlan(PlanNode previous)
        {
            this.Observer.ProduceQueryPlan(new AlterLifetimePlanNode(
                previous, this,
                typeof(TKey), typeof(TPayload), this.startTimeSelector, this.startTimeDurationSelector,
                true));
        }

        public unsafe override void OnNext(StreamMessage<TKey, TPayload> batch)
        {
            batch.vsync = batch.vsync.MakeWritable(this.pool.longPool);
            batch.vother = batch.vother.MakeWritable(this.pool.longPool);
            batch.bitvector = batch.bitvector.MakeWritable(this.pool.bitvectorPool);

            var count = batch.Count;

            fixed (long* vsync = batch.vsync.col)
            fixed (long* vother = batch.vother.col)
            fixed (long* bv = batch.bitvector.col)
            {
                for (int i = 0; i < count; i++)
                {
                    int index;
                    TPartitionKey partition;
                    if ((bv[i >> 6] & (1L << (i & 0x3f))) != 0)
                    {
                        if (vother[i] == PartitionedStreamEvent.LowWatermarkOtherTime)
                        {
                            if ((this.startTimeSelector != null) && (vsync[i] < StreamEvent.InfinitySyncTime))
                            {
                                vsync[i] = this.startTimeSelectorCompiled(vsync[i]);
                                if (vsync[i] > StreamEvent.MaxSyncTime) throw new ArgumentOutOfRangeException();
                            }

                            var iter = FastDictionary<TPartitionKey, long>.IteratorStart;
                            while (this.lastSync.Iterate(ref iter))
                            {
                                if (this.lastSync.entries[iter].value < vsync[i]) this.lastSync.entries[iter].value = vsync[i];
                            }
                        }
                        else if (vother[i] == PartitionedStreamEvent.PunctuationOtherTime)
                        {
                            partition = this.getPartitionKey(batch.key.col[i]);
                            if (this.startTimeSelector != null) vsync[i] = this.startTimeSelectorCompiled(vsync[i]);
                            if (this.lastSync.Lookup(partition, out index))
                                this.lastSync.entries[index].value = Math.Max(vsync[i], this.lastSync.entries[index].value);
                            else
                                this.lastSync.Insert(ref index, partition, vsync[i]);
                        }
                        continue;
                    }

                    partition = this.getPartitionKey(batch.key.col[i]);
                    if (vsync[i] < vother[i])
                    {
                        // insert event
                        var old_vsync = vsync[i];
                        if (this.startTimeSelector != null)
                        {
                            vsync[i] = this.startTimeSelectorCompiled(vsync[i]);
                        }

                        vother[i] = vsync[i] + this.startTimeDurationSelectorCompiled(old_vsync);
                        if (vother[i] > StreamEvent.MaxSyncTime) throw new ArgumentOutOfRangeException();
                        if (vother[i] <= vsync[i]) bv[i >> 6] |= (1L << (i & 0x3f));
                    }
                    else
                    {
                        if (vother[i] != long.MinValue) // not a CTI
                        {
                            // drop the retract
                            bv[i >> 6] |= (1L << (i & 0x3f));
                            continue;
                        }
                        else if (this.startTimeSelector != null)
                        {
                            vsync[i] = this.startTimeSelectorCompiled(vsync[i]);
                        }
                    }
                    if (!this.lastSync.Lookup(partition, out index))
                    {
                        this.lastSync.Insert(ref index, partition, vsync[i]);
                    }
                    else if (vsync[i] < this.lastSync.entries[index].value)
                    {
                        throw new InvalidOperationException(
                            "The operator AlterLifetime produced output out of sync-time order on an input event. The current internal sync time is " + this.lastSync.entries[index].value
                          + ". The event's sync time is " + vsync[i].ToString(CultureInfo.InvariantCulture)
                          + ". The event's partition key is " + partition.ToString()
                          + ". The event's value is " + batch[i].ToString() + ".");
                    }
                    else
                    {
                        this.lastSync.entries[index].value = vsync[i];
                    }
                }
            }

            this.Observer.OnNext(batch);
        }

        public override int CurrentlyBufferedOutputCount => 0;

        public override int CurrentlyBufferedInputCount => 0;
    }
}
