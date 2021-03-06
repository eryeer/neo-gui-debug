﻿using Akka.Actor;
using Akka.Configuration;
using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Actors;
using Neo.IO.Caching;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Neo.Network.P2P
{
    internal class ProtocolHandler : UntypedActor
    {
        public class SetFilter { public BloomFilter Filter; }

        private readonly NeoSystem system;
        private readonly FIFOSet<UInt256> knownHashes;
        private readonly FIFOSet<UInt256> sentHashes;
        private VersionPayload version;
        private bool verack = false;
        private BloomFilter bloom_filter;

        public ProtocolHandler(NeoSystem system)
        {
            this.system = system;
            this.knownHashes = new FIFOSet<UInt256>(Blockchain.Singleton.MemPool.Capacity * 2);
            this.sentHashes = new FIFOSet<UInt256>(Blockchain.Singleton.MemPool.Capacity * 2);
        }

        protected override void OnReceive(object message)
        {
            if (!(message is Message msg)) return;
            foreach (IP2PPlugin plugin in Plugin.P2PPlugins)
                if (!plugin.OnP2PMessage(msg))
                    return;
            if (version == null)
            {
                if (msg.Command != "version")
                    throw new ProtocolViolationException();
                OnVersionMessageReceived(msg.GetPayload<VersionPayload>());
                return;
            }
            if (!verack)
            {
                if (msg.Command != "verack")
                    throw new ProtocolViolationException();
                OnVerackMessageReceived();
                return;
            }
            switch (msg.Command)
            {
                case "addr":
                    OnAddrMessageReceived(msg.GetPayload<AddrPayload>());
                    break;
                case "block":
                    OnInventoryReceived(msg.GetPayload<Block>());
                    break;
                case "consensus":
                    OnInventoryReceived(msg.GetPayload<ConsensusPayload>());
                    break;
                case "filteradd":
                    OnFilterAddMessageReceived(msg.GetPayload<FilterAddPayload>());
                    break;
                case "filterclear":
                    OnFilterClearMessageReceived();
                    break;
                case "filterload":
                    OnFilterLoadMessageReceived(msg.GetPayload<FilterLoadPayload>());
                    break;
                case "getaddr":
                    OnGetAddrMessageReceived();
                    break;
                case "getblocks":
                    OnGetBlocksMessageReceived(msg.GetPayload<GetBlocksPayload>());
                    break;
                case "getdata":
                    OnGetDataMessageReceived(msg.GetPayload<InvPayload>());
                    break;
                case "getheaders":
                    OnGetHeadersMessageReceived(msg.GetPayload<GetBlocksPayload>());
                    break;
                case "headers":
                    OnHeadersMessageReceived(msg.GetPayload<HeadersPayload>());
                    break;
                case "inv":
                    OnInvMessageReceived(msg.GetPayload<InvPayload>());
                    break;
                case "mempool":
                    OnMemPoolMessageReceived();
                    break;
                case "ping":
                    OnPingMessageReceived(msg.GetPayload<PingPayload>());
                    break;
                case "pong":
                    OnPongMessageReceived(msg.GetPayload<PingPayload>());
                    break;
                case "tx":
                    if (msg.Payload.Length <= Transaction.MaxTransactionSize)
                        OnInventoryReceived(msg.GetTransaction());
                    break;
                case "verack":
                case "version":
                    throw new ProtocolViolationException();
                case "alert":
                case "merkleblock":
                case "notfound":
                case "reject":
                default:
                    //暂时忽略
                    break;
            }
        }

        private void OnAddrMessageReceived(AddrPayload payload)
        {
            system.LocalNode.Tell(new Peer.Peers
            {
                EndPoints = payload.AddressList.Select(p => p.EndPoint)
            });
        }

        private void OnFilterAddMessageReceived(FilterAddPayload payload)
        {
            if (bloom_filter != null)
                bloom_filter.Add(payload.Data);
        }

        private void OnFilterClearMessageReceived()
        {
            bloom_filter = null;
            Context.Parent.Tell(new SetFilter { Filter = null });
        }

        private void OnFilterLoadMessageReceived(FilterLoadPayload payload)
        {
            bloom_filter = new BloomFilter(payload.Filter.Length * 8, payload.K, payload.Tweak, payload.Filter);
            Context.Parent.Tell(new SetFilter { Filter = bloom_filter });
        }

        private void OnGetAddrMessageReceived()
        {
            Random rand = new Random();
            IEnumerable<RemoteNode> peers = LocalNode.Singleton.RemoteNodes.Values
                .Where(p => p.ListenerPort > 0)
                .GroupBy(p => p.Remote.Address, (k, g) => g.First())
                .OrderBy(p => rand.Next())
                .Take(AddrPayload.MaxCountToSend);
            NetworkAddressWithTime[] networkAddresses = peers.Select(p => NetworkAddressWithTime.Create(p.Listener, p.Version.Services, p.Version.Timestamp)).ToArray();
            if (networkAddresses.Length == 0) return;
            Context.Parent.Tell(Message.Create("addr", AddrPayload.Create(networkAddresses)));
        }

        private void OnGetBlocksMessageReceived(GetBlocksPayload payload)
        {
            UInt256 hash = payload.HashStart[0];
            if (hash == payload.HashStop) return;
            BlockState state = Blockchain.Singleton.Store.GetBlocks().TryGet(hash);
            if (state == null) return;
            List<UInt256> hashes = new List<UInt256>();
            for (uint i = 1; i <= InvPayload.MaxHashesCount; i++)
            {
                uint index = state.TrimmedBlock.Index + i;
                if (index > Blockchain.Singleton.Height)
                    break;
                hash = Blockchain.Singleton.GetBlockHash(index);
                if (hash == null) break;
                if (hash == payload.HashStop) break;
                hashes.Add(hash);
            }
            if (hashes.Count == 0) return;
            Context.Parent.Tell(Message.Create("inv", InvPayload.Create(InventoryType.Block, hashes.ToArray())));
        }

        private void OnGetDataMessageReceived(InvPayload payload)
        {
            UInt256[] hashes = payload.Hashes.Where(p => sentHashes.Add(p)).ToArray();
            foreach (UInt256 hash in hashes)
            {
                //先从缓存中尝试获取tx
                Blockchain.Singleton.RelayCache.TryGet(hash, out IInventory inventory);
                switch (payload.Type)
                {
                    case InventoryType.TX:
                        //如果缓存中没有tx，则从db中获取tx
                        if (inventory == null)
                            inventory = Blockchain.Singleton.GetTransaction(hash);
                        if (inventory is Transaction)
                            Context.Parent.Tell(Message.Create("tx", inventory));
                        break;
                    case InventoryType.Block:
                        if (inventory == null)
                            inventory = Blockchain.Singleton.GetBlock(hash);
                        if (inventory is Block block)
                        {
                            if (bloom_filter == null)
                            {
                                Context.Parent.Tell(Message.Create("block", inventory));
                            }
                            else
                            {
                                BitArray flags = new BitArray(block.Transactions.Select(p => bloom_filter.Test(p)).ToArray());
                                Context.Parent.Tell(Message.Create("merkleblock", MerkleBlockPayload.Create(block, flags)));
                            }
                        }
                        break;
                    case InventoryType.Consensus:
                        if (inventory != null)
                            Context.Parent.Tell(Message.Create("consensus", inventory));
                        break;
                }
            }
        }

        private void OnGetHeadersMessageReceived(GetBlocksPayload payload)
        {
            UInt256 hash = payload.HashStart[0];
            if (hash == payload.HashStop) return;
            DataCache<UInt256, BlockState> cache = Blockchain.Singleton.Store.GetBlocks();
            BlockState state = cache.TryGet(hash);
            if (state == null) return;
            List<Header> headers = new List<Header>();
            for (uint i = 1; i <= HeadersPayload.MaxHeadersCount; i++)
            {
                uint index = state.TrimmedBlock.Index + i;
                hash = Blockchain.Singleton.GetBlockHash(index);
                if (hash == null) break;
                if (hash == payload.HashStop) break;
                Header header = cache.TryGet(hash)?.TrimmedBlock.Header;
                if (header == null) break;
                headers.Add(header);
            }
            if (headers.Count == 0) return;
            Context.Parent.Tell(Message.Create("headers", HeadersPayload.Create(headers)));
        }

        private void OnHeadersMessageReceived(HeadersPayload payload)
        {
            if (payload.Headers.Length == 0) return;
            system.Blockchain.Tell(payload.Headers, Context.Parent);
        }

        private void OnInventoryReceived(IInventory inventory)
        {
            //通知taskmanager 任务结束。
            system.TaskManager.Tell(new TaskManager.TaskCompleted { Hash = inventory.Hash }, Context.Parent);
            if (inventory is MinerTransaction) return;
            //校验tx，添加到memoryPool，并转发其他节点
            system.LocalNode.Tell(new LocalNode.Relay { Inventory = inventory });
        }

        private void OnInvMessageReceived(InvPayload payload)
        {
            //将tx的hash添加到已知hash集合
            UInt256[] hashes = payload.Hashes.Where(p => knownHashes.Add(p)).ToArray();
            if (hashes.Length == 0) return;
            switch (payload.Type)
            {
                case InventoryType.Block:
                    using (Snapshot snapshot = Blockchain.Singleton.GetSnapshot())
                        hashes = hashes.Where(p => !snapshot.ContainsBlock(p)).ToArray();
                    break;
                case InventoryType.TX:
                    //排除当前db的snapshot中存在的tx的hash
                    using (Snapshot snapshot = Blockchain.Singleton.GetSnapshot())
                        hashes = hashes.Where(p => !snapshot.ContainsTransaction(p)).ToArray();
                    break;
            }
            if (hashes.Length == 0) return;
            //调用TaskManager发起获取tx的task
            system.TaskManager.Tell(new TaskManager.NewTasks { Payload = InvPayload.Create(payload.Type, hashes) }, Context.Parent);
        }

        private void OnMemPoolMessageReceived()
        {
            foreach (InvPayload payload in InvPayload.CreateGroup(InventoryType.TX, Blockchain.Singleton.MemPool.GetVerifiedTransactions().Select(p => p.Hash).ToArray()))
                Context.Parent.Tell(Message.Create("inv", payload));
        }

        private void OnPingMessageReceived(PingPayload payload)
        {
            Context.Parent.Tell(payload);
            Context.Parent.Tell(Message.Create("pong", PingPayload.Create(Blockchain.Singleton.Height, payload.Nonce)));
        }

        private void OnPongMessageReceived(PingPayload payload)
        {
            Context.Parent.Tell(payload);
        }

        private void OnVerackMessageReceived()
        {
            verack = true;
            Context.Parent.Tell("verack");
        }

        private void OnVersionMessageReceived(VersionPayload payload)
        {
            version = payload;
            Context.Parent.Tell(payload);
        }

        public static Props Props(NeoSystem system)
        {
            return Akka.Actor.Props.Create(() => new ProtocolHandler(system)).WithMailbox("protocol-handler-mailbox");
        }
    }

    internal class ProtocolHandlerMailbox : PriorityMailbox
    {
        public ProtocolHandlerMailbox(Akka.Actor.Settings settings, Config config)
            : base(settings, config)
        {
        }

        protected override bool IsHighPriority(object message)
        {
            if (!(message is Message msg)) return true;
            switch (msg.Command)
            {
                case "consensus":
                case "filteradd":
                case "filterclear":
                case "filterload":
                case "verack":
                case "version":
                case "alert":
                    return true;
                default:
                    return false;
            }
        }

        protected override bool ShallDrop(object message, IEnumerable queue)
        {
            if (!(message is Message msg)) return false;
            switch (msg.Command)
            {
                case "getaddr":
                case "getblocks":
                case "getdata":
                case "getheaders":
                case "mempool":
                    return queue.OfType<Message>().Any(p => p.Command == msg.Command);
                default:
                    return false;
            }
        }
    }
}
