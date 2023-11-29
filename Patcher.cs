using EndianExtension;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Hacktice
{
    internal class Patcher
    {
        readonly byte[] _rom;

        public Patcher(byte[] rom)
        {
            _rom = rom;
        }

        static string AppendToFileName(string source, string appendValue)
        {
            return $"{Path.Combine(Path.GetDirectoryName(source), Path.GetFileNameWithoutExtension(source))}{appendValue}{Path.GetExtension(source)}";
        }

        public void WriteConfig(Config cfg)
        {
            int configLocation = 0;
            foreach (var location in MemFind.All(_rom, (uint)((int)Canary.MagicFirst).ToBigEndian()))
            {
                if (_rom.Skip(location).Take(Canary.MagicSwapped.Length).SequenceEqual(Canary.MagicSwapped))
                {
                    configLocation = location;
                    break;
                }
            }

            if (0 == configLocation)
            {
                throw new Exception("Failed to find config location!");
            }

            var size = Marshal.SizeOf(typeof(Config));
            var bytes = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(cfg, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
            Marshal.FreeHGlobal(ptr);

            Endian.CopyByteswap(bytes, 0, _rom, configLocation, size);
        }

        public void Save(string path)
        {
            N64CRC crcCalculator = new N64CRC();
            crcCalculator.crc(_rom);

            var hackticePath = AppendToFileName(path, ".rel");
            File.WriteAllBytes(hackticePath, _rom);
        }
    }
}
