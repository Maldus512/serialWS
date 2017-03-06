using SerialWS;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;


namespace  PacketManagement//SerialWS
{

    class Constants
    {
        public static int SOH = 0x01;
        public static int EOL = 0x04;
    }

    class PacketManager
    {
        public ObservableCollection<Packet> receivedPackets { get; }

        public Dictionary<UInt16, string> CommandNames;

        public ObservableCollection<Packet> getReceivedPacketsList { get
            {
                return receivedPackets;
            }
        }

        //True if we are currently reading a packat
        private bool evaluating;
        //Buffer in which we are storing the packet
        private byte[] UARTBuffer;
        //Current state of reading the packet
        private int ReadIndex;
        //Packet length, read from the fifth and sixth byte
        private int expectedLen;

        private Int64 timeout;

        public PacketManager(Dictionary<UInt16, string> comms)
        {
            receivedPackets = new ObservableCollection<Packet>();
            CommandNames = comms;
            evaluating = false;
            UARTBuffer = new byte[2048 + 12];
            ReadIndex = 0;
        }

        public string CommandsToCSV() {
            string res = "";
            foreach (KeyValuePair<UInt16, string> val in CommandNames) {
                res += "0x" + val.Key.ToString("X4") + " ; " + val.Value +"\r\n";
            }
            return res;
        }

        public void evalNewData(byte[] data)
        {
            int i = 0, len;
            Packet packet;
            Int64 now = Stopwatch.GetTimestamp();

            if (timeout != 0 && now - timeout > 100) {
                evaluating = false;
            }

            //if the remaining part of the packet we are expecting is all in the current buffer
            if (evaluating && expectedLen > ReadIndex + data.Length ) {
                Array.Copy(data, 0, UARTBuffer, ReadIndex, data.Length);
                ReadIndex += data.Length;
                packet = validatePacket(UARTBuffer);
                if (packet != null) {
                    receivedPackets.Insert(0, packet);
                }
                return;
            }

            while (i < data.Length)
            {
                if (!evaluating && data[i] == Constants.SOH && data.Length >= i + 12) {
                    timeout = Stopwatch.GetTimestamp();
                    evaluating = true;
                    ReadIndex = 0;
                    expectedLen = (ushort)(data[i + 5] << 8 | data[i + 6]) + 12;
                    UARTBuffer = new byte[expectedLen];
                }

                if (evaluating && data.Length >= i + (expectedLen - ReadIndex)) {
                    Array.Copy(data, i, UARTBuffer, 0, expectedLen);
                    packet = validatePacket(UARTBuffer);
                    if (packet != null) {
                        receivedPackets.Insert(0, packet);
                    }
                    i += expectedLen - ReadIndex;
                    evaluating = false;
                    ReadIndex = 0;
                    expectedLen = 0;
                }
                else if (evaluating) {
                    Array.Copy(data, i, UARTBuffer, 0, data.Length);
                    ReadIndex += data.Length;
                    break;
                } else {
                    i++;
                }
            }
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
            c = new Command(CommandNames.ContainsKey(comm) ?
                CommandNames[comm] : "Unknown command", comm);
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

    class Packet
        {
        public byte senderAdd { get; set; }
        public byte receiverAdd { get; set; }
        public Command command { get; set; }
        public byte[] payload { get; set; }
        public string hexPayload { get { return BitConverter.ToString(payload); } }
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
