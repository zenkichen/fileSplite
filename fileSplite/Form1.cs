using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace fileSplite
{
    public partial class Form1 : Form
    {
        private const ushort CRC16_POLYNOMIAL = 0x1021; /*CRC_16校验方式的多项式*/
        private const byte Cdd = 0xDD;
        private const byte C60 = 0x60;
        private const byte Caa = 0xAA;
        private const byte C51 = 0x51;
        private const byte C01 = 0x01;
        private const byte C20 = 0x20;
        private const ushort Cffff = 0xFFFF;

        private ushort[] Table_CRC = new ushort[256];
        private string file;
        //private FileStream aFile, bFile;
        private byte[] buff = new byte[60];
        private byte[] pkgBuff = new byte[69];

        private TcpClient tcpClient;
        private TcpClient tcpDownClient=null;
        private Boolean isListening = true;

        public Form1()
        {
            InitializeComponent();
            CRC16Init();
        }

        private void btnSelFile_Click(object sender, EventArgs e)
        {
            OpenFileDialog fileDialog = new OpenFileDialog();
            fileDialog.Multiselect = true;
            fileDialog.Title = "请选择文件";
            fileDialog.Filter = "所有文件(*.*)|*.*";
            if (fileDialog.ShowDialog() == DialogResult.OK)
            {
                file = fileDialog.FileName;
                txtFileName.Text = file;
            }
            btnSender.Enabled = true;
        }

        void CRC16Init()
        {
            uint i, j;
            uint nData;
            uint nAccum;

            for (i = 0; i < 256; i = i + 1)
            {
                nData = (uint)(i << 8);
                nAccum = 0;
                for (j = 0; j < 8; j = j + 1)
                {
                    if (((nData ^ nAccum) & 0x8000) != 0)
                        nAccum = (nAccum << 1) ^ (CRC16_POLYNOMIAL);
                    else
                        nAccum <<= 1;
                    nData <<= 1;
                }
                Table_CRC[i] = (ushort)nAccum;
            }
        }

        /**********************************************
        * 反转数据的比特位, 反转后MSB为1. *
        * 反转前: 1110100011101110 0010100111100000 *
        * 反转后: 1111001010001110 1110001011100000 *
        ***********************************************/
        ulong CRCBitReflect(ulong ulData, int nBits)
        {
            ulong ulResult = 0x00000000L;
            int n;

            for (n = 0; n < nBits; n = n + 1)
            {
                if ((ulData & 0x00000001L) != 0)
                {
                    ulResult |= (ulong)(1L << ((nBits - 1) - n));
                }
                ulData = (ulData >> 1);
            }
            return (ulResult);
        }

        /*******************************************************
        *以CRC_16方式计算一个数据块的CRC值.         *
        *pucData - 待校验的数据块指针.                            *
        *nBytes - 数据块大小, 单位是字节.                       *
        *返回值是无符号的长整型, 其中低16位有效. *
        *********************************************************/
        byte[] CRC16Calc(byte[] aData, int aSize)
        {
            int i;
            byte[] crc = new byte[2];
            uint nAccum = 0xffff;
            for (i = 0; i < aSize; i++)
                nAccum = ((nAccum & 0x00FF) << 8) ^ (uint)Table_CRC[(nAccum >> 8) ^ aData[i]];
            crc[0] = (byte)(nAccum & 0x00FF);
            crc[1] = (byte)(nAccum >> 8);
            return crc;
        }

        public static string ToHexString(byte[] bytes) // 0xae00cf => "AE00CF "
        {
            string hexString = string.Empty;
            if (bytes != null)
            {
                StringBuilder strB = new StringBuilder();

                for (int i = 0; i < bytes.Length; i++)
                {
                    strB.Append(bytes[i].ToString("X2")+" ");
                }
                hexString = strB.ToString();
            }
            return hexString;
        }

        private void btnSender_Click(object sender, EventArgs e)
        {
            FileStream aFile,bFile,cFile;
            BinaryWriter bw,cw;
            ushort packCnt = 0,lengthTmp;
            long i;
            //byte dataNull = 0x20;
            byte[] inputBytes = new byte[507];
            byte[] cFileBytes = new byte[501];
            byte[] crcArray = new byte[496];
            byte[] CRC = new byte[2];
            byte CHK_OR;//异或校验
            string outfile = file.Substring(0, file.LastIndexOf("."));

            aFile = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read);
            BinaryReader br = new BinaryReader(aFile);
            cFile = new FileStream(outfile + "outall" + ".o", FileMode.OpenOrCreate, FileAccess.Write);
            cw = new BinaryWriter(cFile);

            //// 首先判断，文件是否已经存在
            //if (File.Exists("test.o"))
            //{
            //    // 如果文件已经存在，那么删除掉.
            //    File.Delete("test.o");
            //}
            
            //BinaryWriter bw = new BinaryWriter(bFile);
            progressBar1.Value = 0;
            progressBar1.Visible = true;
            //NetworkStream ns = tcpClient.GetStream();

            //如果读取的文件小于496字节
            while ((aFile.Length - aFile.Position) > 496)//每包真实数据长度
            {
                inputBytes[0] = 0x1D;//头
                inputBytes[1] = 0x97;//头
                inputBytes[2] = 0x44;//字
                inputBytes[3] = (byte)(501 >> 8);//长度512-6
                inputBytes[4] = (byte)(501 & 0x00FF);
                if (packCnt == 0) inputBytes[5] = 0xA1;//第一包
                else inputBytes[5] = 0xA2;//中间包
                inputBytes[6] = (byte)(packCnt >> 8);//包序号
                inputBytes[7] = (byte)(packCnt & 0x00FF);//包序号
                try
                {
                    for (i = 0; i < 496; i++)
                    {
                        inputBytes[8 + i] = br.ReadByte();//数据
                    }
                }
                catch (IOException ex)
                {
                }
                Array.Copy(inputBytes,8, crcArray,0, 496);
                CRC = CRC16Calc(crcArray, 496);//计算CRC
                inputBytes[504] = CRC[1];
                inputBytes[505] = CRC[0];
                CHK_OR = (byte)0x00;
                for (i = 2; i < 506; i++)
                {
                    CHK_OR ^= inputBytes[i];
                }
                inputBytes[506] = CHK_OR;//校验和

                bFile = new FileStream(outfile + "out" + packCnt + ".up", FileMode.OpenOrCreate, FileAccess.Write);
                bw = new BinaryWriter(bFile);
                Array.Copy(inputBytes, 5, cFileBytes, 0, 501);
                bw.Write(inputBytes, 0, 507);
                cw.Write(cFileBytes, 0, 501);

                bw.Close();
                bFile.Close();
                packCnt++;
                /*
                pkgBuff[0]=Cdd;//网络发出
                pkgBuff[1]=C60;
                for (i = 0; i < 65;i++ )
                {
                    pkgBuff[2 + i] = inputBytes[i];
                }
                pkgBuff[67] = CRC[1];
                pkgBuff[68] = CRC[0];
                ns.Write(pkgBuff, 0, 69);
                Thread.Sleep(1000);
                 * */
                progressBar1.Value = (int)(aFile.Position * 100 / aFile.Length);//进度条
            }
            lengthTmp = (ushort)(aFile.Length - aFile.Position);//获取剩余字节数

            inputBytes[0] = 0x1D;//头
            inputBytes[1] = 0x97;//头
            inputBytes[2] = 0x44;//字
            inputBytes[3] = (byte)((lengthTmp + 5) >> 8);//长度lengthTmp+5
            inputBytes[4] = (byte)((lengthTmp + 5) & 0x00FF);
            inputBytes[5] = 0xA3;//末尾包
            inputBytes[6] = (byte)(packCnt >> 8);//包序号
            inputBytes[7] = (byte)(packCnt & 0x00FF);//包序号
            try
            {
                for (i = 0; i < lengthTmp; i++)
                {
                    inputBytes[8 + i] = br.ReadByte();//数据
                }
            }
            catch (IOException ex)
            {
            }
            Array.Copy(inputBytes, 8, crcArray, 0, lengthTmp);
            CRC = CRC16Calc(crcArray, lengthTmp);//计算CRC
            inputBytes[lengthTmp+8] = CRC[1];
            inputBytes[lengthTmp+9] = CRC[0];
            CHK_OR = (byte)0x00;
            for (i = 2; i < lengthTmp + 10; i++)
            {
                CHK_OR ^= inputBytes[i];
            }
            inputBytes[lengthTmp+10] = CHK_OR;//校验和

            bFile = new FileStream(outfile + "out" + packCnt + ".up", FileMode.OpenOrCreate, FileAccess.Write);
            bw = new BinaryWriter(bFile);
            bw.Write(inputBytes, 0, (lengthTmp + 11));
            Array.Copy(inputBytes, 5, cFileBytes, 0, (lengthTmp + 5));
            cw.Write(cFileBytes, 0, (lengthTmp + 5));
            /*
            pkgBuff[0] = Cdd;//网络发出
            pkgBuff[1] = C60;
            for (i = 0; i < 65; i++)
            {
                pkgBuff[2 + i] = inputBytes[i];
            }
            pkgBuff[67] = CRC[1];
            pkgBuff[68] = CRC[0];
            ns.Write(pkgBuff, 0, 69);
            Thread.Sleep(1000);
             * */
            progressBar1.Value = (int)(aFile.Position * 100 / aFile.Length);//进度条

            MessageBox.Show("完成");
            progressBar1.Visible = false;
            bw.Close();
            br.Close();
            aFile.Close();
            bFile.Close();
            aFile = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read);
            br = new BinaryReader(aFile);
            CHK_OR = 0x00;
            for(i=0;i<aFile.Length;i++)
            {
                CHK_OR ^= br.ReadByte();
            }
            bFile = new FileStream(outfile + ".chk", FileMode.OpenOrCreate, FileAccess.Write);
            bw = new BinaryWriter(bFile);
            bw.Write(CHK_OR);
            bw.Close();
            br.Close();
            aFile.Close();
            bFile.Close();
            cw.Close();
            cFile.Close();
            //ns.Close();
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                tcpClient = new TcpClientWithTimeout(txtIPaddr.Text, Int32.Parse(txtPort.Text), 5000).Connect();
                //tcpDownClient = new TcpClientWithTimeout(txtIPaddr.Text, Int32.Parse(textBox1.Text), 5000).Connect();
                btnDisconnect.Enabled = true;
                btnConnect.Enabled = false;
                btnSelFile.Enabled = true;
            }
            catch (Exception)
            {
                MessageBox.Show("连接超时！");
            }
            isListening = true;
            Thread thread = new Thread(new ThreadStart(SocketListen));
            thread.Start();
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            tcpClient.Close();
            //tcpDownClient.Close();
            isListening = false;
            btnDisconnect.Enabled = false;
            btnConnect.Enabled = true;
            btnSelFile.Enabled = false;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            FileStream aFile;
            aFile = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read);
            byte[] inputBytes = new byte[aFile.Length];
            BinaryReader br = new BinaryReader(aFile);
            NetworkStream ns = tcpClient.GetStream();
            try
            {
                for (int i = 0; i < aFile.Length; i++)
                {
                    inputBytes[i] = br.ReadByte();
                }
                ns.Write(inputBytes, 0, (int)aFile.Length);
            }
            catch (IOException ex)
            {
            }
            br.Close();
            aFile.Close();
            ns.Close();
        }
        protected delegate void UpdateDisplayDelegate(byte[] text);
        public void SocketListen()
        {
            Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            listener.Bind(new IPEndPoint(IPAddress.Parse(txtIPaddr.Text), Int32.Parse(textBox1.Text)));
            while (isListening)
            {
                listener.Listen(0);

                Socket socket = listener.Accept();
                Stream netStream = new NetworkStream(socket);
                StreamReader reader = new StreamReader(netStream);

                string result = reader.ReadToEnd();
                Invoke(new UpdateDisplayDelegate(UpdateDisplay), new object[] { result });
                socket.Close();
            }
            listener.Close();
        }
        public void UpdateDisplay(byte[] text)
        {
            richTextBox1.Text = ToHexString(text);
        }
    }
}
