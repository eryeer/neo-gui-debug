using Neo.Network.P2P.Payloads;
using Neo.Properties;
using Neo.SmartContract;
using Neo.VM;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows.Forms;
using VMArray = Neo.VM.Types.Array;

namespace Neo.UI
{
    public partial class TransferDialog : Form
    {
        private string remark = "";

        public Fixed8 Fee => Fixed8.Parse(textBox1.Text);
        public UInt160 ChangeAddress => ((string)comboBox1.SelectedItem).ToScriptHash();

        public TransferDialog()
        {
            InitializeComponent();
            textBox1.Text = "0";
            comboBox1.Items.AddRange(Program.CurrentWallet.GetAccounts().Select(p => p.Address).ToArray());
            comboBox1.SelectedItem = Program.CurrentWallet.GetChangeAddress().ToAddress();
        }

        public Transaction GetTransaction()
        {
            //获取非全局资产的cOutput
            var cOutputs = txOutListBox1.Items.Where(p => p.AssetId is UInt160).GroupBy(p => new
            {
                AssetId = (UInt160)p.AssetId,
                Account = p.ScriptHash
            }, (k, g) => new
            {
                k.AssetId,
                Value = g.Aggregate(BigInteger.Zero, (x, y) => x + y.Value.Value),
                k.Account
            }).ToArray();
            Transaction tx;
            List<TransactionAttribute> attributes = new List<TransactionAttribute>();
            //cOutputs个数为零时代表交易为全局资产交易
            if (cOutputs.Length == 0)
            {
                tx = new ContractTransaction();
            }
            else
            //否则为非全局资产交易
            {
                //获取账户中所有地址
                UInt160[] addresses = Program.CurrentWallet.GetAccounts().Select(p => p.ScriptHash).ToArray();
                HashSet<UInt160> sAttributes = new HashSet<UInt160>();
                using (ScriptBuilder sb = new ScriptBuilder())
                {
                    foreach (var output in cOutputs)
                    {
                        byte[] script;

                        //获取账户中各个地址在合约中的金额
                        using (ScriptBuilder sb2 = new ScriptBuilder())
                        {
                            foreach (UInt160 address in addresses)
                                sb2.EmitAppCall(output.AssetId, "balanceOf", address);
                            sb2.Emit(OpCode.DEPTH, OpCode.PACK);
                            script = sb2.ToArray();
                        }
                        ApplicationEngine engine = ApplicationEngine.Run(script);
                        if (engine.State.HasFlag(VMState.FAULT)) return null;
                        var balances = ((VMArray)engine.ResultStack.Pop()).AsEnumerable().Reverse().Zip(addresses, (i, a) => new
                        {
                            Account = a,
                            Value = i.GetBigInteger()
                        }).ToArray();
                        BigInteger sum = balances.Aggregate(BigInteger.Zero, (x, y) => x + y.Value);
                        //如果各地址金额总和小于转账金额，则直接返回
                        if (sum < output.Value) return null;
                        //input'最少原则'
                        //如果各地址金额总和大于转账金额，则按金额大小倒排各地址，按金额从大到小逐一抵扣转帐金额，
                        //直到一个地址的金额足以支付转账金额的抵扣余额，这时会找一个地址的金额与转账金额的抵扣余额差值最小的
                        //一条地址金额，与其他的抵扣地址合并为输入金额。
                        if (sum != output.Value)
                        {
                            balances = balances.OrderByDescending(p => p.Value).ToArray();
                            BigInteger amount = output.Value;
                            int i = 0;
                            while (balances[i].Value <= amount)
                                amount -= balances[i++].Value;
                            if (amount == BigInteger.Zero)
                                balances = balances.Take(i).ToArray();
                            else
                                balances = balances.Take(i).Concat(new[] { balances.Last(p => p.Value >= amount) }).ToArray();
                            sum = balances.Aggregate(BigInteger.Zero, (x, y) => x + y.Value);
                        }
                        sAttributes.UnionWith(balances.Select(p => p.Account));
                        //组装转账脚本
                        for (int i = 0; i < balances.Length; i++)
                        {
                            BigInteger value = balances[i].Value;
                            if (i == 0)
                            {
                                //将找零给了第一个地址
                                BigInteger change = sum - output.Value;
                                if (change > 0) value -= change;
                            }
                            sb.EmitAppCall(output.AssetId, "transfer", balances[i].Account, output.Account, value);
                            sb.Emit(OpCode.THROWIFNOT);
                        }
                    }
                    //将交易封装成InvocationTransaction类型
                    tx = new InvocationTransaction
                    {
                        Version = 1,
                        Script = sb.ToArray()
                    };
                }
                //添加交易属性
                attributes.AddRange(sAttributes.Select(p => new TransactionAttribute
                {
                    Usage = TransactionAttributeUsage.Script,
                    Data = p.ToArray()
                }));
            }
            if (!string.IsNullOrEmpty(remark))
                attributes.Add(new TransactionAttribute
                {
                    Usage = TransactionAttributeUsage.Remark,
                    Data = Encoding.UTF8.GetBytes(remark)
                });
            tx.Attributes = attributes.ToArray();
            tx.Outputs = txOutListBox1.Items.Where(p => p.AssetId is UInt256).Select(p => p.ToTxOutput()).ToArray();
            //处理全局资产交易，封装input、找零output
            if (tx is ContractTransaction ctx)
                tx = Program.CurrentWallet.MakeTransaction(ctx, change_address: ChangeAddress, fee: Fee);
            return tx;
        }

        private void txOutListBox1_ItemsChanged(object sender, EventArgs e)
        {
            button3.Enabled = txOutListBox1.ItemCount > 0;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            remark = InputBox.Show(Strings.EnterRemarkMessage, Strings.EnterRemarkTitle, remark);
        }

        private void button2_Click(object sender, EventArgs e)
        {
            button2.Visible = false;
            groupBox1.Visible = true;
            this.Height = 510;
        }
    }
}
