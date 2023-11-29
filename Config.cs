using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Hacktice
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Player
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] name;
    };

    public struct Score
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] score;
    };

    public struct Status
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] curRound;
        public int internalState;
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 2)]
        public Score[] scores;
        public int pendingScore;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public uint[] prints;
    };

    public struct Team
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] teamName;
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 5)]
        public Player[] players;
    };

    public struct Answer
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 28)]
        public byte[] name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] cost;
    };

    public struct Round
    {
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 8)]
        public Answer[] answers;
    };

    public struct FinalRound
    {
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 5)]
        public Answer[] answersInit;
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 5)]
        public Answer[] answersAfter;
    };

    [StructLayout(LayoutKind.Sequential)]
    public class Config
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] header;
        public Status state;
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 2)]
        public Team[] teams;
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 5)]
        public Round[] rounds;
        public FinalRound final;

        public static byte[] TextToBytes(string name, int size)
        {
            byte[] customText = new byte[size];
            for (int i = 0; i < size; i++)
            {
                int ibase = i / 4;
                int ioff = i % 4;
                int namePos = ibase * 4 + (3 - ioff);
                if (namePos < name.Length)
                {
                    customText[i] = (byte) name[namePos];
                }
                else
                {
                    customText[i] = 0;
                }
            }
            customText[size - 4] = 0;

            return customText;
        }

        public static string BytesToText(byte[] customText)
        {
            StringBuilder builder = new StringBuilder();
            try
            {
                for (int i = 0; i < customText.Length; i++)
                {
                    int ibase = i / 4;
                    int ioff = i % 4;
                    int namePos = ibase * 4 + (3 - ioff);
                    byte b = customText[namePos];
                    if (b != 0)
                    {
                        builder.Append((char)b);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            { }
            return builder.ToString();
        }

        public bool Equal(Status state)
        {
            return state.internalState == this.state.internalState
                && state.scores[0].score.SequenceEqual(this.state.scores[0].score)
                && state.scores[1].score.SequenceEqual(this.state.scores[1].score)
                && state.curRound[0] == this.state.curRound[0]
                && state.pendingScore == this.state.pendingScore
                && state.prints.SequenceEqual(this.state.prints);
        }
    }
}
