using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hacktice
{
    internal class Canary
    {
        public const uint BinaryRamMagic = 0x3C1A8032;

        public const uint MagicFirst = 0x54484520;
        public static readonly byte[] Magic = { 0x20, 0x45, 0x48, 0x54,
                                                0x49, 0x4D, 0x41, 0x46,
                                                0x46, 0x20, 0x59, 0x4C,
                                                0x20, 0x44, 0x55, 0x45,
                                                0x54, 0x4E, 0x4F, 0x43,
                                                0x20, 0x4C, 0x4F, 0x52,
                                                0x52, 0x41, 0x54, 0x53,
                                                0x45, 0x59, 0x20, 0x54, };

        public static readonly byte[] MagicSwapped = { 0x54, 0x48, 0x45, 0x20,
                                                       0x46, 0x41, 0x4D, 0x49,
                                                       0x4C, 0x59, 0x20, 0x46,
                                                       0x45, 0x55, 0x44, 0x20,
                                                       0x43, 0x4F, 0x4E, 0x54,
                                                       0x52, 0x4F, 0x4C, 0x20,
                                                       0x53, 0x54, 0x41, 0x52,
                                                       0x54, 0x20, 0x59, 0x45 };
    }
}
