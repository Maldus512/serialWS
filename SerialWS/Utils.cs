using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using SerialWS.Exceptions;

namespace SerialWS
{
    static class Constants {
        public static string SETTINGSKEY = "settings";
        public static string COMMANDCHOICEKEY = "commandChoice";
        public static string BAUDKEY = "baudChoice";
        public static string SENDERKEY = "sender";
        public static string RECEIVERKEY = "receiver";
        public static string IPADDR = "ip_address";
        public static string PORT = "port";

        public static int PAYLOADLEN = 2048;
    }

    static class Utils
    {
        public static uint packSize = 2048;
        
        public static List<byte[]> formPackets(byte[] content, byte sender, byte receiver, byte[] command)
        {
            List<byte[]> result = new List<byte[]>();
            int i = 0;
            int j = 0;
            uint x;
            byte[] tmp, toCrc;

            do
            {
                x = content.Length - i < packSize ? (uint)content.Length : packSize;
                tmp = new byte[x + 12];
                toCrc = new byte[x + 12 - 5];
                tmp[0] = toCrc[0] = 0x01;
                tmp[1] = toCrc[1] = sender;
                tmp[2] = toCrc[2] = receiver;
                tmp[3] = toCrc[3] = command[0];
                tmp[4] = toCrc[4] = command[1];
                tmp[5] = toCrc[5] = (byte)((x >> 8) & 0xff);
                tmp[6] = toCrc[6] = (byte)(x & 0xff);
                j = 7;
                for (j = 7; j < x + 7; j++)
                {
                    tmp[j] = toCrc[j] = content[i];
                    i++;
                }

                foreach (byte b in crc32(toCrc, toCrc.Length))
                {
                    tmp[j] = b;
                    j++;
                }

                tmp[j] = 0x04;

                result.Add(tmp);
            }
            while (i < content.Length) ;

            return result;
        }

        public static byte[] crc32(byte[] buffer, int len)
        {
            int i, j;
            uint b, crc, mask;
            byte[] res;

            i = 0;
            crc = 0xFFFFFFFF;

            while (i < len)
            {
                b = (uint) buffer[i]; // Get next byte.
                crc = crc ^ b;

                for (j = 7; j >= 0; j--)
                { // Do eight times.
                    mask = (uint)-((uint)(crc & 0x01u));
                    crc = (crc >> 1) ^ (0xEDB88320 & mask);
                }
                i = i + 1;
            }
            res = new byte[4];
            res[3] = (byte)~(crc & 0xff);
            res[2] = (byte)~(crc>>8 & 0xff);
            res[1] = (byte)~(crc>>16 & 0xff);
            res[0] = (byte)~(crc>>24 & 0xff);
            return res;
        }


        /// <summary>
        /// Loads the byte data from a StorageFile
        /// </summary>
        /// <param name="file">The file to read</param>

        public static async Task<byte[]> ReadFile(StorageFile file)

        {

            byte[] fileBytes = null;
            using (IRandomAccessStreamWithContentType stream = await file.OpenReadAsync())
            {
                fileBytes = new byte[stream.Size];
                using (DataReader reader = new DataReader(stream))
                {
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(fileBytes);
                }
            }

            return fileBytes;
        }

        public static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }

        public static string GetTimestamp(DateTime value) {
            return value.ToString("HH:mm:ss");
        }
        
        public static Tuple<UInt16, string> FindInCommandList(UInt16 code, List <Tuple<UInt16, string>> list) {
            foreach(Tuple<UInt16, string> t in list) {
                if (t.Item1 == code) {
                    return t;
                }
            }
            return null;
        }

        public static List<Tuple<UInt16, string>> readCSV(string csv) {
            csv = Regex.Replace(csv,"\r", "");
            ushort code;
            string name;
            string[] content;
            string[] lines = csv.Split('\n');
            List<Tuple<ushort, string>> result = new List<Tuple<ushort, string>>();
            foreach (string val in lines) {
                content = val.Split(';');
                if (content.Length < 2)
                    continue;
                if (content[0].Contains("0x")) {
                    content[0] = Regex.Replace(content[0], "0x", "");
                    code = UInt16.Parse(content[0], System.Globalization.NumberStyles.HexNumber);
                }
                else {
                    code = UInt16.Parse(content[0]);
                }
                if (code > 0xFFFF)
                    throw new InvalidCSVException(code.ToString() + " code is too big");
                name = content[1];
                Tuple<UInt16, string> t = FindInCommandList(code, result);
                if (t == null) {
                    result.Add(new Tuple<ushort, string>(code, name));
                } else {
                    result.Add(new Tuple<UInt16, string>(code, t.Item2));
                }
            }
            return result;
        }


        public static bool IsKeyHex(Windows.System.VirtualKey k) {
            if (k == Windows.System.VirtualKey.A || 
                k == Windows.System.VirtualKey.B || 
                k == Windows.System.VirtualKey.C || 
                k == Windows.System.VirtualKey.D || 
                k == Windows.System.VirtualKey.E || 
                k == Windows.System.VirtualKey.F || 
                k == Windows.System.VirtualKey.Number0 || 
                k == Windows.System.VirtualKey.Number1 || 
                k == Windows.System.VirtualKey.Number2 || 
                k == Windows.System.VirtualKey.Number3 || 
                k == Windows.System.VirtualKey.Number4 || 
                k == Windows.System.VirtualKey.Number5 || 
                k == Windows.System.VirtualKey.Number6 || 
                k == Windows.System.VirtualKey.Number7 || 
                k == Windows.System.VirtualKey.Number8 || 
                k == Windows.System.VirtualKey.Number9 ||
                k == Windows.System.VirtualKey.Space || 
                k == Windows.System.VirtualKey.Subtract) { 
                return true;
            } else {
                return false;
            }
        }

        public static string ConvertHex(String hexString) {
            try {
                string ascii = string.Empty;

                for (int i = 0; i < hexString.Length; i += 2) {
                    String hs = string.Empty;

                    hs = hexString.Substring(i, 2);
                    uint decval = System.Convert.ToUInt32(hs, 16);
                    char character = System.Convert.ToChar(decval);
                    ascii += character;

                }

                return ascii;
            }
            catch (Exception ex1) {  }

            return string.Empty;
        }

    }
      
}
