﻿using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Json;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.VM;
using Neo.Wallets;
using System;
using System.IO;

namespace Neo.Network.P2P.Payloads
{
    public abstract class BlockBase : IVerifiable
    {
        public uint Version;
        public UInt256 PrevHash;
        public UInt256 MerkleRoot;
        public uint Timestamp;
        public uint Index;
        public ulong ConsensusData;
        public UInt160 NextConsensus;
        public Witness Witness;

        private UInt256 _hash = null;
        public UInt256 Hash
        {
            get
            {
                if (_hash == null)
                {
                    _hash = new UInt256(Crypto.Default.Hash256(this.GetHashData()));
                }
                return _hash;
            }
        }

        Witness[] IVerifiable.Witnesses
        {
            get
            {
                return new[] { Witness };
            }
        }

        public virtual int Size => sizeof(uint) + PrevHash.Size + MerkleRoot.Size + sizeof(uint) + sizeof(uint) + sizeof(ulong) + NextConsensus.Size + 1 + Witness.Size;

        public virtual void Deserialize(BinaryReader reader)
        {
            ((IVerifiable)this).DeserializeUnsigned(reader);
            if (reader.ReadByte() != 1) throw new FormatException();
            Witness = reader.ReadSerializable<Witness>();
        }

        void IVerifiable.DeserializeUnsigned(BinaryReader reader)
        {
            Version = reader.ReadUInt32();
            PrevHash = reader.ReadSerializable<UInt256>();
            MerkleRoot = reader.ReadSerializable<UInt256>();
            Timestamp = reader.ReadUInt32();
            Index = reader.ReadUInt32();
            ConsensusData = reader.ReadUInt64();
            NextConsensus = reader.ReadSerializable<UInt160>();
        }

        byte[] IScriptContainer.GetMessage()
        {
            return this.GetHashData();
        }

        UInt160[] IVerifiable.GetScriptHashesForVerifying(Snapshot snapshot)
        {
            if (PrevHash == UInt256.Zero)
                return new[] { Witness.ScriptHash };
            Header prev_header = snapshot.GetHeader(PrevHash);
            if (prev_header == null) throw new InvalidOperationException();
            return new UInt160[] { prev_header.NextConsensus };
        }

        public virtual void Serialize(BinaryWriter writer)
        {
            ((IVerifiable)this).SerializeUnsigned(writer);
            writer.Write((byte)1); writer.Write(Witness);
        }

        void IVerifiable.SerializeUnsigned(BinaryWriter writer)
        {
            writer.Write(Version);
            writer.Write(PrevHash);
            writer.Write(MerkleRoot);
            writer.Write(Timestamp);
            writer.Write(Index);
            writer.Write(ConsensusData);
            writer.Write(NextConsensus);
        }

        public virtual JObject ToJson()
        {
            JObject json = new JObject();
            json["hash"] = Hash.ToString();
            json["size"] = Size;
            json["version"] = Version;
            json["previousblockhash"] = PrevHash.ToString();
            json["merkleroot"] = MerkleRoot.ToString();
            json["time"] = Timestamp;
            json["index"] = Index;
            json["nonce"] = ConsensusData.ToString("x16");
            json["nextconsensus"] = NextConsensus.ToAddress();
            json["script"] = Witness.ToJson();
            return json;
        }

        public virtual bool Verify(Snapshot snapshot)
        {
            Header prev_header = snapshot.GetHeader(PrevHash);
            //检查上一个区块是否存在
            if (prev_header == null) return false;
            //检查上一个区块高度，是否等于当前区块高度-1
            if (prev_header.Index + 1 != Index) return false;
            //检查上一个区块时间戳，是否小于当前区块时间戳
            if (prev_header.Timestamp >= Timestamp) return false;
            //见证人脚本为上一区块的NextConsensus字段，即多方签名脚本
            if (!this.VerifyWitnesses(snapshot)) return false;
            return true;
        }
    }
}
