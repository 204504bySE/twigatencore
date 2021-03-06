﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Net;
using System.IO;
using System.Data;
using MySqlConnector;
using System.Threading;
using System.Threading.Tasks.Dataflow;

using CoreTweet;
using Twigaten.Lib;
using System.Diagnostics;

namespace Twigaten.Tool
{
    class Program
    {
        static async Task Main(string[] args)
        {
            //CheckOldProcess.CheckandExit();
            DBHandler db = new DBHandler();

            if (args.Length == 0)
            {
                await db.RemoveOldMedia();
                await db.RemoveOrphanMedia();
                await db.RemoveOldProfileImage();
            }
            else
            {
                await CommandLine.Run(args).ConfigureAwait(false);
            }
        }
    }


    class DBHandler : Lib.DBHandler
    {
        public DBHandler() : base(config.database.Address, config.database.Protocol) { }

        //ツイートが削除されて参照されなくなった画像を消す
        public async Task RemoveOrphanMedia()
        {
            int RemovedCount = 0;
            const int BulkUnit = 1000;

            try
            {
                var Table = new List<(long media_id, string media_url)>(BulkUnit);
                do
                {
                    Table.Clear();  //ループ判定が後ろにあるのでここでやるしかない
                    using (MySqlCommand cmd = new MySqlCommand(@"SELECT m.media_id, mt.media_url
FROM media m
LEFT JOIN media_downloaded_at md ON m.media_id = md.media_id
JOIN media_text mt ON m.media_id = mt.media_id
WHERE m.source_tweet_id IS NULL
AND (md.downloaded_at IS NULL OR md.downloaded_at < @downloaded_at)
ORDER BY m.media_id
LIMIT @limit;"))
                    {
                        //ダウンロードしたての画像は除く
                        cmd.Parameters.Add("@downloaded_at", MySqlDbType.Int64).Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
                        cmd.Parameters.AddWithValue("@limit", BulkUnit);
                        if (!await ExecuteReader(cmd, (r) => Table.Add((r.GetInt64(0), r.GetString(1))))) { return; }
                    }
                    if (Table.Count < 1) { break; }

                    var DeleteMediaBlock = new ActionBlock<(long media_id, string media_url)>(async (row) =>
                    {
                        File.Delete(MediaFolderPath.ThumbPath(row.media_id, row.media_url));

                        using var DeleteCmd = new MySqlCommand(@"DELETE FROM media WHERE media_id = @media_id");
                        using var DeleteCmd2 = new MySqlCommand(@"DELETE FROM media_text WHERE media_id = @media_id");
                        DeleteCmd.Parameters.Add("@media_id", MySqlDbType.Int64).Value = row.media_id;
                        DeleteCmd2.Parameters.Add("@media_id", MySqlDbType.Int64).Value = row.media_id;

                        int deleted = await ExecuteNonQuery(new[] { DeleteCmd, DeleteCmd2 }).ConfigureAwait(false);
                        if (deleted > 0) { Interlocked.Add(ref RemovedCount, deleted >> 1); }
                    }, new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount });

                    foreach (var row in Table) { DeleteMediaBlock.Post(row); }
                    DeleteMediaBlock.Complete();
                    await DeleteMediaBlock.Completion.ConfigureAwait(false);
                    //Console.WriteLine("{0} Orphan Media removed.", RemovedCount);
                } while (Table.Count >= BulkUnit);
            }
            catch (Exception e) { Console.WriteLine(e); return; }
            Console.WriteLine("{0} Orphan Media removed.", RemovedCount);
        }

        public async Task RemoveOldMedia()
        {
            DriveInfo drive = new DriveInfo(config.crawl.PictPaththumb);
            int RemovedCountFile = 0;
            const int BulkUnit = 1000;
            //Console.WriteLine("{0} / {0} MB Free.", drive.AvailableFreeSpace >> 20, drive.TotalSize >> 20);
            try
            {
                var Table = new List<(long media_id, string media_url)>(BulkUnit);
                while (drive.TotalFreeSpace < drive.TotalSize / 16)
                {
                    using (MySqlCommand cmd = new MySqlCommand(@"(SELECT
m.media_id, mt.media_url
FROM media_downloaded_at md
JOIN media m on md.media_id = m.media_id
JOIN media_text mt ON m.media_id = mt.media_id
ORDER BY downloaded_at
LIMIT @limit)
ORDER BY media_id;"))
                    {
                        cmd.Parameters.AddWithValue("@limit", BulkUnit);
                        if (!await ExecuteReader(cmd, (r) => Table.Add((r.GetInt64(0), r.GetString(1))))) { return; }
                    }
                    if (Table.Count < BulkUnit) { break; }

                    foreach (var row in Table)
                    {
                        File.Delete(MediaFolderPath.ThumbPath(row.media_id, row.media_url));
                    }

                    using (var Cmd = new MySqlCommand(BulkCmdStrIn(Table.Count, @"DELETE FROM media_downloaded_at WHERE media_id IN")))
                    {
                        for (int i = 0; i < Table.Count; i++)
                        {
                            Cmd.Parameters.Add("@" + i.ToString(), DbType.Int64).Value = Table[i].media_id;
                        }
                        await ExecuteNonQuery(Cmd).ConfigureAwait(false);
                    }
                    RemovedCountFile += Table.Count;
                    //Console.WriteLine("{0} Media removed", RemovedCountFile);
                    //Console.WriteLine("{0} / {1} MB Free.", drive.AvailableFreeSpace >> 20, drive.TotalSize >> 20);
                    Table.Clear();
                }
            }
            catch (Exception e) { Console.WriteLine(e); return; }
            Console.WriteLine("{0} Old Media removed.", RemovedCountFile);
        }

        //しばらくツイートがないアカウントのprofile_imageを消す
        public async Task RemoveOldProfileImage()
        {
            DriveInfo drive = new DriveInfo(config.crawl.PictPathProfileImage);
            int RemovedCount = 0;
            const int BulkUnit = 1000;
            const string head = @"DELETE FROM user_updated_at WHERE user_id IN";
            string BulkUpdateCmd = BulkCmdStrIn(BulkUnit, head);
            //Console.WriteLine("{0} / {1} MB Free.", drive.AvailableFreeSpace >> 20, drive.TotalSize >> 20);
            try
            {
                var Table = new List<(long user_id, string profile_image_url, bool is_default_profile_image)>(BulkUnit);
                while (drive.TotalFreeSpace < drive.TotalSize / 16)
                {
                    using (var cmd = new MySqlCommand(@"SELECT
user_id, profile_image_url, is_default_profile_image
FROM user
JOIN user_updated_at USING (user_id)
WHERE profile_image_url IS NOT NULL
ORDER BY updated_at LIMIT @limit;"))
                    {
                        cmd.Parameters.AddWithValue("@limit", BulkUnit);
                        if (!await ExecuteReader(cmd, (r) => Table.Add((r.GetInt64(0), r.GetString(1), r.GetBoolean(2))))) { return; }
                    }
                    if (Table.Count < BulkUnit) { break; }

                    foreach (var row in Table)
                    {
                        if (!row.is_default_profile_image)
                        {
                            File.Delete(MediaFolderPath.ProfileImagePath(row.user_id, row.is_default_profile_image, row.profile_image_url));
                        }
                    }
                    using (var upcmd = new MySqlCommand(BulkUpdateCmd))
                    {
                        for (int n = 0; n < Table.Count; n++)
                        {
                            upcmd.Parameters.Add("@" + n.ToString(), DbType.Int64).Value = Table[n].user_id;
                        }
                        RemovedCount += await ExecuteNonQuery(upcmd);
                    }
                    //Console.WriteLine("{0} Icons removed", RemovedCount);
                    //Console.WriteLine("{0} / {1} MB Free.", drive.AvailableFreeSpace >> 20, drive.TotalSize >> 20);
                    Table.Clear();
                }
            }
            catch (Exception e) { Console.WriteLine(e); return; }
            Console.WriteLine("{0} Icons removed.", RemovedCount);
        }

        public async Task<(long MinDownloadedAt,string[] MediaPath)> GetMediaPath(long downloaded_at)
        {
            using (var cmd = new MySqlCommand(@"SELECT
m.media_id, mt.media_url
FROM media m
JOIN media_downloaded_at md ON m.media_id = md.media_id
JOIN media_text mt ON m.media_id = mt.media_id
WHERE md.downloaded_at <= @downloaded_at
ORDER BY md.downloaded_at DESC
LIMIT @limit"))
            {
                cmd.Parameters.AddWithValue("@downloaded_at", downloaded_at);
                cmd.Parameters.AddWithValue("@limit", 1000);

                var ret = new List<string>();
                long minDownloadedAt = long.MaxValue;
                await ExecuteReader(cmd, (r) =>
                {
                    long down = r.GetInt64(0);
                    if(down < minDownloadedAt) { minDownloadedAt = down; }
                    ret.Add(MediaFolderPath.ThumbPath(down, r.GetString(1)));
                }).ConfigureAwait(false);
                return (minDownloadedAt, ret.ToArray());
            }
        }

        /*
        // innodb→rocksdbに使ったやつ

        public async Task InsertAllTweet()
        {
            const int bulkunit = 1000;
            const string bulkhead = @"INSERT IGNORE INTO tweet_text (tweet_id, text) VALUES";
            string bulkfullstr = BulkCmdStr(bulkunit, 2, bulkhead);

            long TweetCount = 0;
            long InsertCount = 0;

            var doblock = new ActionBlock<long>(async (snowflake) => {
                var tweetlist = new List<KeyValuePair<long, string>>();
                while (true)
                {
                    using (var getcmd = new MySqlCommand(@"SELECT tweet_id, text FROM tweet USE INDEX(PRIMARY) WHERE tweet_id BETWEEN @begin AND @end AND text IS NOT NULL;"))
                    {
                        getcmd.Parameters.Add("@begin", MySqlDbType.Int64).Value = snowflake;
                        getcmd.Parameters.Add("@end", MySqlDbType.Int64).Value = snowflake + SnowFlake.msinSnowFlake * 1000 * 60 - 1;
                        if (await ExecuteReader(getcmd, (r) => tweetlist.Add(new KeyValuePair<long, string>(r.GetInt64(0), r.GetString(1)))).ConfigureAwait(false)) { break; }
                    }
                }
                int t;
                for (t = 0; t < tweetlist.Count / bulkunit; t++)
                {
                    using (var insertcmd = new MySqlCommand(bulkfullstr))
                    {
                        for (int i = 0; i < bulkunit; i++)
                        {
                            string numstr = i.ToString();
                            var tweet = tweetlist[i + bulkunit * t];
                            insertcmd.Parameters.Add("@a" + numstr, MySqlDbType.Int64).Value = tweet.Key;
                            insertcmd.Parameters.Add("@b" + numstr, MySqlDbType.Text).Value = tweet.Value;
                        }
                        int inserted = await ExecuteNonQuery(insertcmd).ConfigureAwait(false);
                        if (inserted < 0) { t--; continue; }
                        else { Interlocked.Add(ref InsertCount, inserted); }
                    }
                }
                int rest = tweetlist.Count % bulkunit;
                if (rest != 0)
                {
                    do
                    {
                        using (var insertcmd = new MySqlCommand(BulkCmdStr(rest, 2, bulkhead)))
                        {
                            for (int i = 0; i < rest; i++)
                            {
                                string numstr = i.ToString();
                                var tweet = tweetlist[i + bulkunit * t];
                                insertcmd.Parameters.Add("@a" + numstr, MySqlDbType.Int64).Value = tweet.Key;
                                insertcmd.Parameters.Add("@b" + numstr, MySqlDbType.Text).Value = tweet.Value;
                            }
                            int inserted = await ExecuteNonQuery(insertcmd).ConfigureAwait(false);
                            if (inserted < 0) { continue; }
                            else { Interlocked.Add(ref InsertCount, inserted); }
                        }
                    }
                    while (false);
                }
                Interlocked.Add(ref TweetCount, tweetlist.Count);
            }, new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = 4, BoundedCapacity = 16 });

            var sw = Stopwatch.StartNew();

            long endsnowflake = SnowFlake.SecondinSnowFlake(DateTimeOffset.FromUnixTimeSeconds(1558841972), false);
            long snowflakecount;
            for (snowflakecount = 1131945314723442689; snowflakecount < endsnowflake; snowflakecount += SnowFlake.msinSnowFlake * 1000 * 60)
            {
                await doblock.SendAsync(snowflakecount).ConfigureAwait(false);
                if (sw.ElapsedMilliseconds >= 60000)
                {
                    Console.WriteLine("{0}\t{1} / {2}\t{3}", DateTime.Now, InsertCount, TweetCount, snowflakecount);
                    sw.Restart();
                }
            }
            doblock.Complete();
            await doblock.Completion.ConfigureAwait(false);
            Console.WriteLine("{0}\t{1} / {2}\t{3}", DateTime.Now, InsertCount, TweetCount, snowflakecount);
            Console.WriteLine("＼(^o^)／");
        }

        public async Task InsertAllMedia()
        {
            const int bulkunit = 1000;
            const string bulkhead = @"INSERT IGNORE INTO media_text (media_id, type, media_url) VALUES";
            string bulkfullstr = BulkCmdStr(bulkunit, 3, bulkhead);

            long MediaCount = 0;
            long InsertCount = 0;

            var doblock = new ActionBlock<long>(async (snowflake) => {
                var tweetlist = new List<(long media_id, string type, string media_url)>();
                while (true)
                {
                    using (var getcmd = new MySqlCommand(@"SELECT media_id, type, media_url FROM media USE INDEX(PRIMARY) WHERE media_id BETWEEN @begin AND @end AND type != '';"))
                    {
                        getcmd.Parameters.Add("@begin", MySqlDbType.Int64).Value = snowflake;
                        getcmd.Parameters.Add("@end", MySqlDbType.Int64).Value = snowflake + SnowFlake.msinSnowFlake * 1000 * 60 - 1;
                        if (await ExecuteReader(getcmd, (r) => tweetlist.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)))).ConfigureAwait(false)) { break; }
                    }
                }
                int t;
                for (t = 0; t < tweetlist.Count / bulkunit; t++)
                {
                    using (var insertcmd = new MySqlCommand(bulkfullstr))
                    {
                        for (int i = 0; i < bulkunit; i++)
                        {
                            string numstr = i.ToString();
                            var media = tweetlist[i + bulkunit * t];
                            insertcmd.Parameters.Add("@a" + numstr, MySqlDbType.Int64).Value = media.media_id;
                            insertcmd.Parameters.Add("@b" + numstr, MySqlDbType.VarChar).Value = media.type;
                            insertcmd.Parameters.Add("@c" + numstr, MySqlDbType.Text).Value = media.media_url;
                        }
                        int inserted = await ExecuteNonQuery(insertcmd).ConfigureAwait(false);
                        if (inserted < 0) { t--; continue; }
                        else { Interlocked.Add(ref InsertCount, inserted); }
                    }
                }
                int rest = tweetlist.Count % bulkunit;
                if (rest != 0)
                {
                    do
                    {
                        using (var insertcmd = new MySqlCommand(BulkCmdStr(rest, 3, bulkhead)))
                        {
                            for (int i = 0; i < rest; i++)
                            {
                                string numstr = i.ToString();
                                var media = tweetlist[i + bulkunit * t];
                                insertcmd.Parameters.Add("@a" + numstr, MySqlDbType.Int64).Value = media.media_id;
                                insertcmd.Parameters.Add("@b" + numstr, MySqlDbType.VarChar).Value = media.type;
                                insertcmd.Parameters.Add("@c" + numstr, MySqlDbType.Text).Value = media.media_url;
                            }
                            int inserted = await ExecuteNonQuery(insertcmd).ConfigureAwait(false);
                            if (inserted < 0) { continue; }
                            else { Interlocked.Add(ref InsertCount, inserted); }
                        }
                    }
                    while (false);
                }
                Interlocked.Add(ref MediaCount, tweetlist.Count);
            }, new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = 8, BoundedCapacity = 32 });

            var sw = Stopwatch.StartNew();

            long endsnowflake = SnowFlake.SecondinSnowFlake(DateTimeOffset.FromUnixTimeSeconds(1558791459), false);
            long snowflakecount;
            for (snowflakecount = 1104490407372402689; snowflakecount < endsnowflake; snowflakecount += SnowFlake.msinSnowFlake * 1000 * 60)
            {
                await doblock.SendAsync(snowflakecount).ConfigureAwait(false);
                if (sw.ElapsedMilliseconds >= 60000)
                {
                    Console.WriteLine("{0}\t{1} / {2}\t{3}", DateTime.Now, InsertCount, MediaCount, snowflakecount);
                    sw.Restart();
                }
            }
            doblock.Complete();
            await doblock.Completion.ConfigureAwait(false);
            Console.WriteLine("{0}\t{1} / {2}\t{3}", DateTime.Now, InsertCount, MediaCount, snowflakecount);
            Console.WriteLine("＼(^o^)／");
        }
        */
        /*
                //画像が削除されて意味がなくなったツイートを消す
                //URL転載したやつの転載元ツイートが消された場合
                public int RemoveOrphanTweet()
                {
                    const int BulkUnit = 100;
                    const int RangeSeconds = 300;
                    const string head = @"DELETE FROM tweet WHERE tweet_id IN";
                    string BulkDeleteCmd = BulkCmdStrIn(BulkUnit, head);

                    TransformBlock<long, DataTable> GetTweetBlock = new TransformBlock<long, DataTable>(async (long id) =>
                    {
                        using (MySqlCommand Cmd = new MySqlCommand(@"SELECT tweet_id
        FROM tweet
        WHERE retweet_id IS NULL
        AND NOT EXISTS (SELECT * FROM tweet_media WHERE tweet_media.tweet_id = tweet.tweet_id)
        AND tweet_id BETWEEN @begin AND @end
        ORDER BY tweet_id DESC;"))
                        {
                            Cmd.Parameters.AddWithValue("@begin", id);
                            Cmd.Parameters.AddWithValue("@end", id + SnowFlake.msinSnowFlake * RangeSeconds * 1000 - 1);
                            return await SelectTable(Cmd, IsolationLevel.RepeatableRead);
                        }
                    }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = Environment.ProcessorCount });


                    DateTimeOffset date = DateTimeOffset.UtcNow.AddHours(-1);
                    for (int i = 0; i < 20; i++)
                    {
                        GetTweetBlock.Post(SnowFlake.SecondinSnowFlake(date, false));
                        date = date.AddHours(-1);
                    }
                    while (true)
                    {
                        DataTable Table = GetTweetBlock.Receive();
                        if (Table.Rows.Count > 0)
                        {
                            using (var delcmd = new MySqlCommand(BulkCmdStrIn(Table.Rows.Count, head)))
                            {
                                for (int n = 0; n < Table.Rows.Count; n++)
                                {
                                    delcmd.Parameters.AddWithValue("@" + n.ToString(), Table.Rows[n].Field<long>(0));
                                }
                                Console.WriteLine("{0} {1} Tweets removed", date, ExecuteNonQuery(delcmd));
                            }
                        }
                        GetTweetBlock.Post(SnowFlake.SecondinSnowFlake(date, false));
                        date = date.AddSeconds(-RangeSeconds);
                    }
                }

                //ツイートが削除されて参照されなくなったユーザーを消す
                public async Task<int> RemoveOrphanUser()
                {
                    int RemovedCount = 0;
                    DataTable Table;
                    using (MySqlCommand cmd = new MySqlCommand(@"SELECT user_id, profile_image_url, is_default_profile_image FROM user
        WHERE NOT EXISTS (SELECT * FROM tweet WHERE tweet.user_id = user.user_id)
        AND NOT EXISTS (SELECT user_id FROM token WHERE token.user_id = user.user_id);"))
                    {
                        Table = await SelectTable(cmd, IsolationLevel.ReadUncommitted);
                    }
                    if (Table == null) { return 0; }
                    Console.WriteLine("{0} {1} Users to remove", DateTime.Now, Table.Rows.Count);
                    Console.ReadKey();
                    Parallel.ForEach(Table.AsEnumerable(),
                        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                        async (DataRow row) =>  //これ動かないよ
                        {
                            bool toRemove;
                            using (MySqlCommand cmd = new MySqlCommand(@"SELECT EXISTS(SELECT * FROM tweet WHERE tweet.user_id = @user_id) OR EXISTS(SELECT user_id FROM token WHERE token.user_id = @user_id);"))
                            {
                                cmd.Parameters.AddWithValue("@user_id", row.Field<long>(0));
                                toRemove = (await SelectCount(cmd, IsolationLevel.ReadUncommitted) == 0);
                            }
                            if (toRemove)
                            {
                                using (MySqlCommand cmd = new MySqlCommand(@"DELETE FROM user WHERE user_id = @user_id;"))
                                {
                                    cmd.Parameters.AddWithValue("@user_id", (long)row[0]);
                                    if (await ExecuteNonQuery(cmd) >= 1)
                                    {
                                        if (!row.Field<bool>(2) && row.Field<string>(1) != null) { File.Delete(Path.Combine(config.crawl.PictPathProfileImage, (row.Field<long>(0)).ToString() + Path.GetExtension(row.Field<string>(1)))); }
                                        Interlocked.Increment(ref RemovedCount);
                                        if (RemovedCount % 1000 == 0) { Console.WriteLine("{0} {1} Users Removed", DateTime.Now, RemovedCount); }
                                    }
                                }
                            }
                        });
                    return RemovedCount;
                }


                public async Task RemoveOrphanProfileImage()
                {
                    int RemoveCount = 0;
                    IEnumerable<string> Files = Directory.EnumerateFiles(config.crawl.PictPathProfileImage);
                    ActionBlock<string> RemoveBlock = new ActionBlock<string>(async (f) =>
                    {
                        using (MySqlCommand cmd = new MySqlCommand(@"SELECT COUNT(*) FROM user WHERE user_id = @user_id;"))
                        {
                            cmd.Parameters.AddWithValue("@user_id", Path.GetFileNameWithoutExtension(f));
                            if (await SelectCount(cmd, IsolationLevel.ReadUncommitted) == 0)
                            {
                                File.Delete(f);
                                Interlocked.Increment(ref RemoveCount);
                                Console.WriteLine("{0} {1} Files Removed. Last: {2}", DateTime.Now, RemoveCount, Path.GetFileName(f));
                            }
                        }
                    }, new ExecutionDataflowBlockOptions()
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        BoundedCapacity = Environment.ProcessorCount << 8
                    });
                    foreach(string f in Directory.EnumerateFiles(config.crawl.PictPathProfileImage))
                    {
                        await RemoveBlock.SendAsync(f);
                    }
                    RemoveBlock.Complete();
                    await RemoveBlock.Completion;
                }

                public void ReHashMedia_Dataflow()
                {
                    ServicePointManager.ReusePort = true;
                    const int ConnectionLimit = 64;
                    ServicePointManager.DefaultConnectionLimit = ConnectionLimit * 4;
                    const int BulkUnit = 1000;
                    DataTable Table;
                    int updated = 0;
                    var GetHashBlock = new TransformBlock<KeyValuePair<long, string>, KeyValuePair<long, long?>>(media => {
                        return new KeyValuePair<long, long?>(media.Key, downloadforHash(media.Value + ":thumb"));
                    }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = ConnectionLimit });
                    using (MySqlCommand cmd = new MySqlCommand(@"SELECT media_id, media_url FROM media ORDER BY media_id DESC LIMIT @limit;"))
                    {
                        cmd.Parameters.AddWithValue("@limit", BulkUnit);
                        Table = SelectTable(cmd,IsolationLevel.ReadUncommitted);
                    }
                    foreach (DataRow row in Table.Rows)
                    {
                        GetHashBlock.Post(new KeyValuePair<long, string>((long)row[0], row[1] as string));
                    }
                    while (Table != null && Table.Rows.Count > 0)
                    {
                        int LastTableCount = Table.Rows.Count;

                        using (MySqlCommand cmd = new MySqlCommand(@"SELECT media_id, media_url FROM media WHERE media_id < @lastid ORDER BY media_id DESC LIMIT @limit;"))
                        {
                            cmd.Parameters.AddWithValue("@lastid", (long)Table.Rows[Table.Rows.Count - 1][0]);
                            cmd.Parameters.AddWithValue("@limit", BulkUnit);
                            Table = SelectTable(cmd, IsolationLevel.ReadUncommitted);
                        }
                        foreach (DataRow row in Table.Rows)
                        {
                            GetHashBlock.Post(new KeyValuePair<long, string>((long)row[0], row[1] as string));
                        }
                        KeyValuePair<long, long?> media = new KeyValuePair<long, long?>(0, null);
                        for (int i = 0; i < LastTableCount; i++)
                        {
                            media = GetHashBlock.Receive();
                            if (media.Value != null)
                            {
                                using (MySqlCommand cmdtmp = new MySqlCommand(@"UPDATE media SET dcthash=@dcthash WHERE media_id = @media_id"))
                                {
                                    cmdtmp.Parameters.AddWithValue("@dcthash", media.Value);
                                    cmdtmp.Parameters.AddWithValue("@media_id", media.Key);
                                    updated += ExecuteNonQuery(cmdtmp);
                                }
                            }
                        }
                        Console.WriteLine("{0} {1} hashes updated. last: {2}", DateTime.Now, updated, media.Key);
                    }
                    GetHashBlock.Complete();
                    while (GetHashBlock.OutputCount > 0)
                    {
                        KeyValuePair<long, long?> media = GetHashBlock.Receive();
                        if (media.Value != null)
                        {
                            using (MySqlCommand cmdtmp = new MySqlCommand(@"UPDATE media SET dcthash=@dcthash WHERE media_id = @media_id"))
                            {
                                cmdtmp.Parameters.AddWithValue("@dcthash", media.Value);
                                cmdtmp.Parameters.AddWithValue("@media_id", media.Key);
                                updated += ExecuteNonQuery(cmdtmp);
                            }
                        }
                    }
                    Console.WriteLine("{0} {1} hashes updated.", DateTime.Now, updated);
                }

                long? downloadforHash(string uri, string referer = null)
                {
                    try
                    {
                        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
                        if (referer != null) { req.Referer = referer; }
                        WebResponse res = req.GetResponse();

                        using (Stream httpStream = res.GetResponseStream())
                        using (MemoryStream mem = new MemoryStream())
                        {
                            httpStream.CopyTo(mem); //MemoryStreamはFlush不要(FlushはNOP)
                            mem.Position = 0;
                            return PictHash.DCTHash(mem);
                        }
                    }
                    catch { return null; }
                }
                */
    }
}

