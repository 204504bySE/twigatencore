﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Buffers;
using Twigaten.Lib;

namespace Twigaten.Hash
{
    ///<summary>ハミング距離が一定以下のハッシュ値のペアを突っ込むやつ</summary>
    public readonly struct HashPair
    {
        public readonly long small;
        public readonly long large;
        public HashPair(long _media0, long _media1)
        {
            if (_media0 < _media1)
            {
                small = _media0;
                large = _media1;
            }
            else if (_media1 < _media0)
            {
                small = _media1;
                large = _media0;
            }
            else { throw new ArgumentException(nameof(_media0) + " == " + nameof(_media1) + ": " + _media0.ToString()); }
        }

        ///<summary>small, large順で比較</summary>
        public static Comparison<HashPair> Comparison { get; } 
            = ((a, b) =>
        {
            if (a.small < b.small) { return -1; }
            else if (a.small > b.small) { return 1; }
            else if (a.large < b.large) { return -1; }
            else if (a.large > b.large) { return 1; }
            else { return 0; }
        });
    }

    /// <summary>
    /// 複合ソート法による全ペア類似度検索 とかいうやつ
    /// http://d.hatena.ne.jp/tb_yasu/20091107/1257593519
    /// </summary>
    class MediaHashSorter
    {
        readonly Config config = Config.Instance;
        readonly HashSet<long> NewHash;
        readonly int MaxHammingDistance;
        readonly Combinations Combi;

        readonly BatchBlock<HashPair> PairBatchBlock = new BatchBlock<HashPair>(DBHandler.StoreMediaPairsUnit);
        readonly ActionBlock<HashPair[]> PairStoreBlock;
        int PairCount;
        int DBAddCount;

        public MediaHashSorter(HashSet<long> NewHash, DBHandler db, int MaxHammingDistance, int ExtraBlock)
        {
            this.NewHash = NewHash; //nullだったら全hashが処理対象
            this.MaxHammingDistance = MaxHammingDistance;
            Combi = new Combinations(MaxHammingDistance + ExtraBlock, ExtraBlock);

            //このブロックは全MultipleSortUnitで共有する
            PairStoreBlock = new ActionBlock<HashPair[]>(
            async (p) =>
            {
                int AddCount;
                do { AddCount = await db.StoreMediaPairs(p).ConfigureAwait(false); } while (AddCount < 0);    //失敗したら無限に再試行
                if (0 < AddCount) { Interlocked.Add(ref DBAddCount, AddCount); }
            },
            new ExecutionDataflowBlockOptions() 
            {
                SingleProducerConstrained = true, 
                MaxDegreeOfParallelism = Environment.ProcessorCount
            });
            PairBatchBlock.LinkTo(PairStoreBlock, new DataflowLinkOptions() { PropagateCompletion = true });
        }

        /// <summary>これを呼ぶと全部やる</summary>
        public async Task Proceed()
        {
            var SortTask = new List<Task>(Combi.Length);
            for (int i = 0; i < Combi.Length; i++)
            {
                SortTask.Add(await MultipleSortUnit(i).ConfigureAwait(false));
            }
            await Task.WhenAll(SortTask).ConfigureAwait(false);
            PairBatchBlock.Complete();
            await PairStoreBlock.Completion.ConfigureAwait(false);
            Console.WriteLine("{0} / {1} Pairs Inserted",DBAddCount, PairCount);
        }

        const int bitcount = sizeof(long) * 8;    //longのbit数
        /// <summary>複合ソート法のマスク1個分をやる</summary>
        /// <param name="Index">nCxの組合せの何個目か(0~(nCx)-1)</param>
        async Task<Task> MultipleSortUnit(int Index)
        {
            int[] BaseBlocks = Combi[Index];
            int StartBlock = BaseBlocks.Last();
            long SortMask = UnMask(BaseBlocks, Combi.Choice);

            var QuickSortSW = Stopwatch.StartNew();

            //適当なサイズに切ってそれぞれをクイックソート
            int SortedFileCount = await SplitQuickSort.QuickSortAll(Index, SortMask).ConfigureAwait(false);
            //↓ベンチマーク用 sortのファイル数を入れてやる
            //int SortedFileCount = 52;

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Default, true, false);
            GC.WaitForPendingFinalizers();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();

            QuickSortSW.Stop();
            Console.WriteLine("{0}\tFile Sort\t{1}ms ", Index, QuickSortSW.ElapsedMilliseconds);

            long[] UnMasks = Enumerable.Range(0, Combi.Choice).Select(i => UnMask(i, Combi.Choice)).ToArray();

            var MultipleSortBlock = new ActionBlock<AddOnlyList<long>>((BlockList) =>
            {
                var Block = BlockList.InnerArray;
                var NewHash = this.NewHash;

                int LocalPairCount = 0;  //見つけた組合せの数を数える
                int BlockIndex = 0;
                int CurrentBlockLength;

                //(要素数,実際の要素,…),0 という配列を読み込んでいく
                for (; (CurrentBlockLength = (int)Block[BlockIndex]) > 0; BlockIndex += CurrentBlockLength + 1)
                {
                    //「実際の要素」1セット分を取り出す
                    var SortedSpan = Block.AsSpan(BlockIndex + 1, CurrentBlockLength);

                    //新しい値を含まないやつは省略
                    if (NewHash != null)
                    {
                        for (int i = 0; i < SortedSpan.Length; i++)
                        {
                            if (NewHash.Contains(SortedSpan[i])) { goto HasNewHash; }
                        }
                        continue;
                    }
                    HasNewHash:

                    for (int i = 0; i < SortedSpan.Length; i++)
                    {
                        long Sorted_i = SortedSpan[i];
                        bool NeedInsert_i = NewHash?.Contains(Sorted_i) ?? true;
                        //long maskedhash_i = Sorted_i & SortMask;
                        for (int j = i + 1; j < SortedSpan.Length; j++)
                        {
                            long Sorted_j = SortedSpan[j];
                            //重複排除を行う(同じハッシュ同士を比較しないだけ)
                            if (Sorted_i == Sorted_j) { continue; }
                            //if (maskedhash_i != (Sorted[j] & FullMask)) { break; }    //これはSortedFileReaderがやってくれる
                            //間違いなくすでにDBに入っているペアは処理しない
                            if ((NeedInsert_i || NewHash.Contains(Sorted_j))    //NewHashがnullなら後者は処理されないからセーフ
                                //ブロックソートで一致した組のハミング距離を測る
                                && BitOperations.PopCount((ulong)(Sorted_i ^ Sorted_j)) <= MaxHammingDistance)
                            {
                                //一致したペアが見つかる最初の組合せを調べる
                                int matchblockindex = 0;
                                int x;
                                for (x = 0; x < UnMasks.Length && x < StartBlock && matchblockindex < BaseBlocks.Length; x++)
                                {
                                    if (BaseBlocks.Contains(x))
                                    {
                                        if (x < BaseBlocks[matchblockindex]) { break; }
                                        matchblockindex++;
                                    }
                                    else
                                    {
                                        if ((Sorted_i & UnMasks[x]) == (Sorted_j & UnMasks[x]))
                                        {
                                            if (x < BaseBlocks[matchblockindex]) { break; }
                                            matchblockindex++;
                                        }
                                    }
                                }
                                //最初の組合せだったときだけ入れる
                                if (x == StartBlock)
                                {
                                    LocalPairCount++;
                                    PairBatchBlock.Post(new HashPair(Sorted_i, Sorted_j));
                                }
                            }
                        }
                    }
                }
                BlockList.Dispose();
                Interlocked.Add(ref PairCount, LocalPairCount);
            }, new ExecutionDataflowBlockOptions()
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                BoundedCapacity = Environment.ProcessorCount << 1,
                SingleProducerConstrained = true
            });

            var MultipleSortSW = Stopwatch.StartNew();

            //クイックソートしたファイル群をマージソートしながら読み込む
            using (var Reader = new MergeSortReader(Index, SortedFileCount, SortMask))
            {
                int PostponeCount = 1 + config.hash.MergeSortPostponePairCount / DBHandler.StoreMediaPairsUnit;
                var GCStopWatch = Stopwatch.StartNew();
                AddOnlyList<long> Sorted;
                while ((Sorted = Reader.ReadBlocks()) != null)
                {
                    await MultipleSortBlock.SendAsync(Sorted).ConfigureAwait(false);
                    //DBにハッシュのペアを入れる処理が詰まったら休んだりGCしたりする
                    while (PostponeCount < PairStoreBlock.InputCount)
                    {
                        if (60000 <= GCStopWatch.ElapsedMilliseconds)
                        {
                            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                            GC.Collect();
                            GCStopWatch.Restart();
                        }
                        await Task.Delay(1000).ConfigureAwait(false); 
                    }
                }
            }
            //余りをDBに入れるついでにここで制御を戻してしまう
            MultipleSortBlock.Complete();
            return MultipleSortBlock.Completion
                .ContinueWith((_) =>
                {
                    MultipleSortSW.Stop();
                    Console.WriteLine("{0}\tMerge+Comp\t{1}ms", Index, MultipleSortSW.ElapsedMilliseconds);
                });

        }

        long UnMask(int block, int blockcount)
        {
            return UnMask(new int[] { block }, blockcount);
        }
        /// <summary>
        /// 64bitを割と均等に分割して,blocksに入った範囲のビットを1にした数値
        /// 例えば blocks={0,2}, blockcount=4 なら 0x0000FFFF0000FFFF
        /// </summary>
        /// <param name="blocks">1で埋めたいブロック(謎)</param>
        /// <param name="blockcount">分割数</param>
        long UnMask(int[] blocks, int blockcount)
        {
            long ret = 0;
            foreach (int b in blocks)
            {
                int maxbit = Math.Min(bitcount * (b + 1) / blockcount, bitcount);
                for (int i = bitcount * b / blockcount; i < maxbit; i++)
                {
                    ret |= 1L << i;
                }
            }
            return ret;
        }
    }
}
