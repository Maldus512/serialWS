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
    }

    static class Utils
    {
        public static uint packSize = 2048;

        public static Dictionary<ushort, string> CommandNames = new Dictionary<ushort, string>()
        {
            /*READ*/
            { 0x0100, "Read versione programma" },
            { 0x0101, "Read dati macchina" },
            { 0x0102, "Read ora" },
            { 0x0500, "Read status macchina" },
            { 0x0501, "Read status programma" },
            { 0x0502, "Read temperatura, livello, velocità" },
            { 0x0503, "Read stato ingressi" },
            { 0x0504, "Read uscite" },
            { 0x0a00, "Read allarmi" },
            { 0x0a01, "Read statistiche" },
            { 0x0f00, "Read parametri riservati HSW" },
            { 0x0f01, "Read programma n" },
            { 0x0f02, "Read nome programma n" },
            { 0x0f03, "Read lista di programmi" },
            { 0x0f04, "Read lista dei nomi programma" },
            /*WRITE*/
            { 0x7800, "Write stop macchina" },
            { 0x7801, "Write start macchina" },
            { 0x7802, "Write programma più" },
            { 0x7803, "Write programma meno" },
            { 0x7804, "Write step piu" },
            { 0x7805, "Write step meno" },
            { 0x7d00, "Write visualizza pagina principale" },
            { 0x7d01, "Write visualizza pagina in/out" },
            { 0x7d02, "Write visualizza pagina versione programma" },
            { 0x8200, "Write visualizza pagina test input" },
            { 0x8205, "Write visualizza pagina test output" },
            { 0x8206, "Write azzera tutti output" },
            { 0x8207, "Write attiva output n" },
            { 0x8208, "Write azzera output n" },
            { 0x820a, "Write visualzza pagina test oblo'" },
            { 0x820b, "Write chiudi oblo'" },
            { 0x820c, "Write apri oblo'" },
            { 0x9600, "Write parametri riservati HSW" },
            { 0x9601, "Write programma n" },
            { 0x9602, "Write nome programma n" },
            { 0x9603, "Write tutti i programmi" },
            { 0x9604, "Write tutti i nomi programma" },
            /*ACK*/
            { 0xf500, "Ack ok" },
            { 0xfa00, "Nack time out" },
            { 0xfa01, "Nack crc errato" },
            { 0xfa02, "Nack comando sconosciuto" },
            { 0xfa03, "Nack non eseguibile" },
            { 0xfa04, "Nack contesto errato" },
            { 0xfa05, "Nack occupato" },
        };



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

        public static Dictionary<UInt16, string> readCSV(string csv) {
            csv = Regex.Replace(csv,"\r", "");
            ushort code;
            string name;
            string[] content;
            string[] lines = csv.Split('\n');
            Dictionary<ushort, string> result = new Dictionary<ushort, string>();
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
                result.Add(code, name);
            }
            return result;
        }

    }
      
}
