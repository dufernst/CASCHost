﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CASCEdit.Helpers;
using CASCEdit.Structs;
using CASCEdit.IO;

namespace CASCEdit.Handlers
{

    public class EncodingHandler : IDisposable
    {
        private const int CHUNK_SIZE = 4096;
        private EncodingMap[] EncodingMap;

        public EncodingHeader Header;
        public List<string> LayoutStringTable = new List<string>();
        public SortedList<MD5Hash, EncodingEntry> Data = new SortedList<MD5Hash, EncodingEntry>(new HashComparer());
        public SortedList<MD5Hash, EncodingLayout> Layout = new SortedList<MD5Hash, EncodingLayout>(new HashComparer());

        public EncodingHandler(BLTEStream blte)
        {
            BinaryReader stream = new BinaryReader(blte);

            Header = new EncodingHeader()
            {
                Magic = stream.ReadBytes(2),
                Version = stream.ReadByte(),
                ChecksumSizeA = stream.ReadByte(),
                ChecksumSizeB = stream.ReadByte(),
                FlagsA = stream.ReadUInt16(),
                FlagsB = stream.ReadUInt16(),
                NumEntriesA = stream.ReadUInt32BE(),
                NumEntriesB = stream.ReadUInt32BE(),
                _9 = stream.ReadByte(),
                StringBlockSize = stream.ReadUInt32BE()
            };

            LayoutStringTable.AddRange(Encoding.ASCII.GetString(stream.ReadBytes((int)Header.StringBlockSize)).Split('\0')); //StringTable

            stream.ReadBytes((int)Header.NumEntriesA * 32); // header block

            for (int i = 0; i < Header.NumEntriesA; i++)
            {
                long start = stream.BaseStream.Position;

                ushort keysCount;
                while ((keysCount = stream.ReadUInt16()) != 0)
                {
                    EncodingEntry entry = new EncodingEntry()
                    {
                        DecompressedSize = stream.ReadUInt32BE(),
                        Hash = new MD5Hash(stream)
                    };

                    for (int ki = 0; ki < keysCount; ki++)
                        entry.Keys.Add(new MD5Hash(stream));

                    Data.Add(entry.Hash, entry);
                }

                if (stream.BaseStream.Position % CHUNK_SIZE != 0)
                    stream.BaseStream.Position += CHUNK_SIZE - ((stream.BaseStream.Position - start) % CHUNK_SIZE);
            }

            stream.ReadBytes((int)Header.NumEntriesB * 32); //Header block

            for (int i = 0; i < Header.NumEntriesB; i++)
            {
                long start = stream.BaseStream.Position;

                MD5Hash hash;
                while (!(hash = new MD5Hash(stream)).IsEmpty)
                {
                    var entry = new EncodingLayout()
                    {
                        Hash = hash,
                        StringIndex = stream.ReadUInt32BE(),
                        Size = stream.ReadUInt40BE()
                    };

                    Layout.Add(entry.Hash, entry);
                }

                if (stream.BaseStream.Position % CHUNK_SIZE != 0)
                    stream.BaseStream.Position += CHUNK_SIZE - ((stream.BaseStream.Position - start) % CHUNK_SIZE);
            }

            stream.ReadBytes((int)(stream.BaseStream.Length - stream.BaseStream.Position)); //EncodingStringTable

            EncodingMap = blte.EncodingMap.ToArray();

            blte?.Dispose();
            stream?.Dispose();
        }


        public void AddEntry(CASCResult blte)
        {
            if (blte == null)
                return;

            //Entry
            var entry = new EncodingEntry()
            {
                DecompressedSize = blte.DecompressedSize,
                Hash = blte.DataHash
            };
            entry.Keys.Add(blte.Hash);

            if (Data.ContainsKey(blte.DataHash))
            {
                var existing = Data[blte.DataHash];

                if (Layout.ContainsKey(existing.Keys[0])) //Remove old layout
                    Layout.Remove(existing.Keys[0]);

                existing.Keys[0] = blte.Hash; //Existing entry
            }                
            else
            {
                Data.Add(entry.Hash, entry); //New entry
            }

            AddLayoutEntry(blte);
        }

        private void AddLayoutEntry(CASCResult blte)
        {
            if (Layout.ContainsKey(blte.Hash))
                Layout.Remove(blte.Hash);

            //Get layout string
            string layoutString;
            uint size = blte.CompressedSize - 30;

            if (blte.DataHash == CASCContainer.BuildConfig.GetKey("root")) //Root
                layoutString = "z";
            else if (size >= 1024 * 256) //256K* seems to be max
                layoutString = "b:{256K*=z}";
            else if (size > 1024)
                layoutString = "b:{" + (int)Math.Floor(size / 1024d) + "K*=z}"; //Closest K
            else
                layoutString = "b:{" + size + "*=z}"; //Bytes

            //String index
            int stridx = LayoutStringTable.IndexOf(layoutString);
            if (stridx == -1)
            {
                stridx = LayoutStringTable.Count - 2;
                LayoutStringTable.Insert(stridx, layoutString);
            }

            //Entry
            var entry = new EncodingLayout()
            {
                Size = size,
                Hash = blte.Hash,
                StringIndex = (uint)stridx
            };
            Layout.Add(entry.Hash, entry);
        }


        public CASCResult Write()
        {
            byte[][] Entries = new byte[EncodingMap.Length][];
            CASCFile[] Files = new CASCFile[EncodingMap.Length];

            //StringTable A 1
            Entries[1] = Encoding.UTF8.GetBytes(string.Join("\0", LayoutStringTable));
            Files[1] = new CASCFile(Entries[1], EncodingMap[1].Type, EncodingMap[1].CompressionLevel);

            //Data Blocks 3
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                long pos = 0;
                foreach (var entry in Data.Values)
                {
                    if (pos + entry.EntrySize > CHUNK_SIZE)
                    {
                        bw.Write(new byte[CHUNK_SIZE - pos]);
                        pos = 0;
                    }

                    bw.Write((ushort)entry.Keys.Count);
                    bw.WriteUInt32BE(entry.DecompressedSize);
                    bw.Write(entry.Hash.Value);
                    for (int i = 0; i < entry.Keys.Count; i++)
                        bw.Write(entry.Keys[i].Value);

                    pos += entry.EntrySize;
                }

                bw.Write(new byte[CHUNK_SIZE - pos]);

                Entries[3] = ms.ToArray();
                Files[3] = new CASCFile(Entries[3], EncodingMap[3].Type, EncodingMap[3].CompressionLevel);
            }

            //Layout Blocks 5
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                long pos = 0;
                foreach (var entry in Layout.Values)
                {
                    if (pos + entry.EntrySize > CHUNK_SIZE)
                    {
                        bw.Write(new byte[CHUNK_SIZE - pos]); //Pad to chunk size
                        pos = 0;
                    }

                    bw.Write(entry.Hash.Value);
                    bw.WriteUInt32BE(entry.StringIndex);
                    bw.WriteUInt40BE(entry.Size);

                    pos += entry.EntrySize;
                }

                //EOF flag
                bw.Write(new byte[16]);
                bw.Write(0xFFFFFFFF);
                pos += 16 + 4;

                bw.Write(new byte[CHUNK_SIZE - pos]); //Final padding

                Entries[5] = ms.ToArray();
                Files[5] = new CASCFile(Entries[5], EncodingMap[5].Type, EncodingMap[5].CompressionLevel);
            }

            //Data Header 2
            using (var md5 = MD5.Create())
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                int chunks = Entries[3].Length / CHUNK_SIZE;

                for (int i = 0; i < chunks; i++)
                {
                    byte[] chunk = new byte[CHUNK_SIZE];
                    Buffer.BlockCopy(Entries[3], (i * CHUNK_SIZE), chunk, 0, CHUNK_SIZE);

                    bw.Write(chunk, 6, 16); //First entry hash
                    bw.Write(md5.ComputeHash(chunk));
                }

                Entries[2] = ms.ToArray();
                Files[2] = new CASCFile(Entries[2], EncodingMap[2].Type, EncodingMap[2].CompressionLevel);
            }

            //Layout Header 4
            using (var md5 = MD5.Create())
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                int chunks = Entries[5].Length / CHUNK_SIZE;

                for (int i = 0; i < chunks; i++)
                {
                    byte[] chunk = new byte[CHUNK_SIZE];
                    Buffer.BlockCopy(Entries[5], (i * CHUNK_SIZE), chunk, 0, CHUNK_SIZE);

                    bw.Write(chunk, 0, 16); //First entry hash
                    bw.Write(md5.ComputeHash(chunk));
                }

                Entries[4] = ms.ToArray();
                Files[4] = new CASCFile(Entries[4], EncodingMap[4].Type, EncodingMap[4].CompressionLevel);
            }

            //Header 0
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(Header.Magic);
                bw.Write(Header.Version);
                bw.Write(Header.ChecksumSizeA);
                bw.Write(Header.ChecksumSizeB);
                bw.Write(Header.FlagsA);
                bw.Write(Header.FlagsB);
                bw.WriteUInt32BE((uint)Entries[2].Length / 32);
                bw.WriteUInt32BE((uint)Entries[4].Length / 32);
                bw.Write(Header._9);
                bw.WriteUInt32BE((uint)Encoding.UTF8.GetByteCount(string.Join("\0", LayoutStringTable)));

                Entries[0] = ms.ToArray();
                Files[0] = new CASCFile(Entries[0], EncodingMap[0].Type, EncodingMap[0].CompressionLevel);
            }

            //StringTableB 6
            Entries[6] = GetStringTable(Entries.Select(x => x.Length));
            Files[6] = new CASCFile(Entries[6], EncodingMap[6].Type, EncodingMap[6].CompressionLevel);

            //Write
            CASCResult res = DataHandler.Write(WriteMode.CDN, Files);
            using (var md5 = MD5.Create())
                res.DataHash = new MD5Hash(md5.ComputeHash(Entries.SelectMany(x => x).ToArray()));

            CASCContainer.Logger.LogInformation($"Encoding: Hash: {res.Hash} Data: {res.DataHash}");

            CASCContainer.BuildConfig.Set("encoding-size", res.DecompressedSize.ToString());
            CASCContainer.BuildConfig.Set("encoding-size", (res.CompressedSize - 30).ToString(), 1); //BLTE size minus header
            CASCContainer.BuildConfig.Set("encoding", res.DataHash.ToString());
            CASCContainer.BuildConfig.Set("encoding", res.Hash.ToString(), 1);

            Entries = new byte[0][];
            Files = new CASCFile[0];
			Entries = null;
			Files = null;

            //Cache Encoding Hash
            CASCContainer.Settings.Cache?.AddOrUpdate(new CacheEntry() { MD5 = res.DataHash, BLTE = res.Hash, Path = "__ENCODING__" });

            return res;
        }
		
        private byte[] GetStringTable(IEnumerable<int> lengths)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("b:{");

            for (int i = 0; i < EncodingMap.Length; i++)
            {
                char encoding = (char)EncodingMap[i].Type;

                if (i == EncodingMap.Length - 1)
                    sb.Append($"*={encoding}");
                else
                    sb.Append($"{lengths.ElementAt(i)}={encoding},");
            }

            sb.Append("}");

            return Encoding.UTF8.GetBytes(sb.ToString().ToLowerInvariant());
        }

        public void Dispose()
        {
            Header = null;
            LayoutStringTable.Clear();
            LayoutStringTable.TrimExcess();
            LayoutStringTable = null;
            Data.Clear();
            Data = null;
            Layout.Clear();
            Layout = null;
        }
    }
}
