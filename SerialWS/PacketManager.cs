using SerialWS;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.System.Threading;

namespace  PacketManagement//SerialWS
{

    interface IAckCallback {
        void OnAck();
        void OnNack(string msg);
    }

    class Constants
    {
        public static int SOH = 0x01;
        public static int EOL = 0x04;
    }

    class PacketManager
    {
        public ObservableCollection<Packet> receivedPackets { get; }

        public List<Tuple<UInt16, string>> CommandNames;

        //True if we are currently reading a packet
        public bool evaluating;
        //Buffer in which we are storing the packet
        private byte[] UARTBuffer;
        //Current state of reading the packet
        private int ReadIndex;
        //Packet length, read from the fifth and sixth byte
        private int expectedLen;

        private Int64 timeoutCounter;
        public IAckCallback callback;

        private Int64 _TIMEOUT = 100;

        public Int64 TIMEOUT {
            get {
                return TIMEOUT;
            }
            set {
                if (checkTimeout != null) {
                    checkTimeout.Cancel();
                    checkTimeout = null;
                }
                _TIMEOUT = value;
            }
        }

        public PacketManager(List<Tuple<UInt16, string>> comms)
        {
            receivedPackets = new ObservableCollection<Packet>();
            CommandNames = comms;
            evaluating = false;
            UARTBuffer = new byte[2048 + 12];
            callback = null;
            ReadIndex = 0;
        }


        public void ClearHistory() {
            receivedPackets.Clear();
        }

        public string CommandsToCSV() {
            string res = "";
            foreach (Tuple<UInt16, string> val in CommandNames) {
                res += "0x" + val.Item1.ToString("X4") + " ; " + val.Item2 +"\r\n";
            }
            return res;
        }

        private ThreadPoolTimer checkTimeout = null;
        private bool alive = false;


        public uint remaining () {
            if (!evaluating) {
                return 0;
            }

            return (uint)expectedLen - (uint)ReadIndex;
        }

        public void reset() {
            evaluating = false;
            if (checkTimeout != null)
                checkTimeout.Cancel();
            checkTimeout = null;
            ReadIndex = 0;
            expectedLen = 0;
        }

        //Returns true if we correctly identified a packet AND QUIT, false otherwise
        public bool evalNewData(byte[] data) {
            int i = 0;
            Packet packet;
            bool b = false;

            TimeSpan period = TimeSpan.FromMilliseconds(_TIMEOUT);
            alive = true;

            if (checkTimeout == null) {
                checkTimeout = ThreadPoolTimer.CreatePeriodicTimer((source) => {
                    if (alive) {
                        alive = false;
                    } else {
                        evaluating = false;
                        reset();
                    }
                }, period);
            }

            //We are not evaluating and about to begin
            while (i < data.Length) {
                if (ReadIndex == 0 && data[i] == Constants.SOH) {
                    evaluating = true;
                    UARTBuffer = new byte[7];
                    UARTBuffer[ReadIndex] = data[i];
                    ReadIndex++;
                    i++;
                } else if (ReadIndex > 0 && ReadIndex <= 5) {
                    UARTBuffer[ReadIndex] = data[i];
                    ReadIndex++;
                    i++;
                } else if (ReadIndex == 6) {
                    UARTBuffer[ReadIndex] = data[i];
                    ReadIndex++;
                    i++;
                    expectedLen = (ushort)(UARTBuffer[5] << 8 | UARTBuffer[6]) + 12;
                    byte[] tmp = new byte[7];
                    Array.Copy(UARTBuffer, tmp, 7);
                    UARTBuffer = new byte[expectedLen];
                    Array.Copy(tmp, UARTBuffer, 7);
                } else if (ReadIndex < expectedLen) {
                    int len = expectedLen - ReadIndex;
                    len = len > data.Length - i ? data.Length - i : len;
                    len = len > UARTBuffer.Length - ReadIndex ? UARTBuffer.Length - ReadIndex : len;
                    Array.Copy(data, i, UARTBuffer, ReadIndex, len);
                    ReadIndex+= len;
                    i+= len;
                } else if (ReadIndex > expectedLen){
                    reset();
                    return false;
                } else {
                    i++;
                }

                if (ReadIndex == expectedLen) {
                    packet = validatePacket(UARTBuffer);
                    evaluating = false;
                    if (packet != null) {
                        b = true;
                        receivedPackets.Insert(0, packet);
                        if (Regex.IsMatch(packet.command.Name.ToUpper(), @"ACK") && 
                            !Regex.IsMatch(packet.command.Name.ToUpper(), @"NACK")) {
                            callback.OnAck();
                        } else {
                            callback.OnNack(packet.command.Name);
                        }
                    }
                    reset();
                }
            }
            return b;// return b;
        }


        private string findCommandByCode(ushort code) {
            foreach(Tuple<UInt16, string> c in CommandNames) {
                if (c.Item1 == code) {
                    return c.Item2;
                }
            }
            return "Unknown command";
        }

        public Packet validatePacket(byte[] packet)
        {
            ushort len, comm;
            Packet result;
            byte s, r;
            Command c;
            byte[] content, crc;
            crc = new byte[4];
            int j = 0;

            if (packet.Length < 12)
                return null;

            if (packet[0] != 0x01)
                return null;

            s = packet[1];
            r = packet[2];
            comm = (ushort)(packet[3] << 8 | packet[4]);
            string tmp = findCommandByCode(comm);
            c = new Command( tmp, comm);
            len = (ushort)(packet[5] << 8 | packet[6]);

            content = new byte[len];
            for (j = 0; j < len; j++) content[j] = packet[7 + j];

            crc = Utils.crc32(packet, 7+len);

            for (j = 0; j < 4; j++)
            {
                if (crc[j] != packet[7+len+j])
                    return null;
            }

            if (packet[7+len+j] != 0x04)
                return null;

            result = new Packet(s, r, c, content);
            return result;
        }

    }

    public class Packet
        {
        public byte senderAdd { get; set; }
        public byte receiverAdd { get; set; }
        public Command command { get; set; }
        public byte[] payload { get; set; }
        public string hexPayload { get { return BitConverter.ToString(payload); } }
        public string asciiPayload { get { return System.Text.Encoding.ASCII.GetString(payload); } }
        public int packetLen { get { return payload.Length + 12; } }
        public string timeStamp { get; set; }

       
        public Packet(byte sender, byte receiver, Command comm, byte[] content)
        {
            senderAdd = sender;
            receiverAdd = receiver;
            command = comm;
            payload = content;
            timeStamp = Utils.GetTimestamp(DateTime.Now);
        }
    }

    public class Command
    {
        private ushort code;
        private string name;

        public string MainCode { get { return BitConverter.ToString(new byte[1] { (byte)(code >> 8) }); } }
        public string SubCode { get { return BitConverter.ToString(new byte[1] { (byte)(code & 0x00FF) }); } }
 
        /*public Command(ushort c) : this(Utils.CommandNames.ContainsKey(c) ? 
            Utils.CommandNames[c] :
            "Unknown command", c)
        {
        }*/

        public Command(string n, ushort c)
        {
            name = n;
            code = c;
        }

        public ushort Code { get { return code; } set { code = value; } }
        public string Name { get { return name; } set { name = value; } }
    }
}
