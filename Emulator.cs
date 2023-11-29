using EndianExtension;
using Hacktice.ProcessExtensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms.VisualStyles;

namespace Hacktice
{
    internal class Emulator
    {
        private Process _process;
        private ulong _ramPtrBase = 0;

        private IntPtr _ptrRam;
        private IntPtr? _ptrConfig;
        private IntPtr _ptrStatus;
        private byte[] _ram; // just a cache

        const int RAMSize = 0x400000;

        static readonly string[] s_ProcessNames = {
            "project64", "project64d",
            "mupen64-rerecording",
            "mupen64-pucrash",
            "mupen64_lua",
            "mupen64-wiivc",
            "mupen64-RTZ",
            "mupen64-rerecording-v2-reset",
            "mupen64-rrv8-avisplit",
            "mupen64-rerecording-v2-reset",
            "mupen64",
            "retroarch" };

        private Process FindEmulatorProcess()
        {
            foreach (string name in s_ProcessNames)
            {
                Process process = Process.GetProcessesByName(name).Where(p => !p.HasExited).FirstOrDefault();
                if (process != null)
                    return process;
            }
            return null;
        }

        public enum PrepareResult
        {
            NOT_FOUND,
            ONLY_EMULATOR,
            OK,
        }

        public PrepareResult Prepare()
        {
            PrepareResult result = PrepareResult.NOT_FOUND;
            try
            {
                if (!(_process is object) || _process.HasExited)
                {
                    _process = FindEmulatorProcess();
                }

                if (!(_process is object))
                {
                    return PrepareResult.NOT_FOUND;
                }

                result = PrepareResult.ONLY_EMULATOR;
                List<long> romPtrBaseSuggestions = new List<long>();
                List<long> ramPtrBaseSuggestions = new List<long>();

                var name = _process.ProcessName.ToLower();
                int offset = 0;

                if (name.Contains("project64") || name.Contains("wine-preloader"))
                {
                    DeepPointer[] ramPtrBaseSuggestionsDPtrs = { new DeepPointer("Project64.exe", 0xD6A1C),     //1.6
                        new DeepPointer("RSP 1.7.dll", 0x4C054), new DeepPointer("RSP 1.7.dll", 0x44B5C),        //2.3.2; 2.4 
                    };

                    DeepPointer[] romPtrBaseSuggestionsDPtrs = { new DeepPointer("Project64.exe", 0xD6A2C),     //1.6
                        new DeepPointer("RSP 1.7.dll", 0x4C050), new DeepPointer("RSP 1.7.dll", 0x44B58)        //2.3.2; 2.4
                    };

                    // Time to generate some addesses for magic check
                    foreach (DeepPointer romSuggestionPtr in romPtrBaseSuggestionsDPtrs)
                    {
                        int ptr = -1;
                        try
                        {
                            ptr = romSuggestionPtr.Deref<int>(_process);
                            romPtrBaseSuggestions.Add(ptr);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }

                    foreach (DeepPointer ramSuggestionPtr in ramPtrBaseSuggestionsDPtrs)
                    {
                        int ptr = -1;
                        try
                        {
                            ptr = ramSuggestionPtr.Deref<int>(_process);
                            ramPtrBaseSuggestions.Add(ptr);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }
                }

                if (name.Contains("mupen64"))
                {
                    if (name == "mupen64")
                    {
                        // Current mupen releases
                        {
                            ramPtrBaseSuggestions.Add(0x00505CB0); // 1.0.9
                            ramPtrBaseSuggestions.Add(0x00505D80); // 1.0.9.1
                            ramPtrBaseSuggestions.Add(0x0050B110); // 1.0.10
                        }
                    }
                    else
                    {
                        // Legacy mupen versions
                        Dictionary<string, int> mupenRAMSuggestions = new Dictionary<string, int>
                    {
                        { "mupen64-rerecording", 0x008EBA80 },
                        { "mupen64-pucrash", 0x00912300 },
                        { "mupen64_lua", 0x00888F60 },
                        { "mupen64-wiivc", 0x00901920 },
                        { "mupen64-RTZ", 0x00901920 },
                        { "mupen64-rrv8-avisplit", 0x008ECBB0 },
                        { "mupen64-rerecording-v2-reset", 0x008ECA90 },
                    };
                        ramPtrBaseSuggestions.Add(mupenRAMSuggestions[name]);
                    }

                    offset = 0x20;
                }

                if (name.Contains("retroarch"))
                {
                    ramPtrBaseSuggestions.Add(0x80000000);
                    romPtrBaseSuggestions.Add(0x90000000);
                    offset = 0x40;
                }

                MagicManager mm = new MagicManager(_process, romPtrBaseSuggestions.ToArray(), ramPtrBaseSuggestions.ToArray(), offset);
                _ramPtrBase = mm.ramPtrBase;
                _ptrRam = new IntPtr((long)_ramPtrBase);
                return PrepareResult.OK;
            }
            catch (Exception)
            { }

            return result;
        }

        private static bool LooksLikeConfigHeader(byte[] header)
        {
            return header.SequenceEqual(Canary.Magic);
        }

        bool RefreshHackticeHeaderAndCheckIfValid()
        {
            if (!Ok())
                return false;

            byte[] config = new byte[Canary.Magic.Length];
            _process.FetchBytes(_ptrConfig.Value, Canary.Magic.Length, config);
            bool ok = LooksLikeConfigHeader(config);
            if (ok)
            {
                _ram = null;
            }

            return ok;
        }

        public bool RefreshHacktice()
        {
            if (_ptrConfig.HasValue)
            {
                // refresh the pointers and check if it is still reasonable
                if (RefreshHackticeHeaderAndCheckIfValid())
                    return true;

                _ptrConfig = null;
            }

            if (!(_ram is object))
            {
                _ram = new byte[RAMSize];
            }
            _process.FetchBytes(_ptrRam, RAMSize, _ram);

            foreach (var location in MemFind.All(_ram, Canary.MagicFirst))
            {
                // attempt all locations and find if any is reasonable
                _ptrConfig = new IntPtr((long)(_ramPtrBase + (ulong)location));
                _ptrStatus = new IntPtr((long)(_ramPtrBase + (ulong)(location + Canary.Magic.Length)));
                if (RefreshHackticeHeaderAndCheckIfValid())
                    return true;
            }

            // it is over
            return false;
        }

        public bool Ok()
        {
            return !_process.HasExited;
        }

        public void Write(Config cfg)
        {
            var size = Marshal.SizeOf(typeof(Config));

            var bytes = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(cfg, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
            Marshal.FreeHGlobal(ptr);

            _process.WriteBytes(_ptrConfig.Value, bytes, new UIntPtr((uint) size));
        }

        public Status ReadState()
        {
            var size = Marshal.SizeOf(typeof(Status));
            var configBytes = _process.ReadBytes(_ptrStatus, size);
            var ptr = Marshal.AllocHGlobal(size);
            Marshal.Copy(configBytes, 0, ptr, size);
            var config = (Status) Marshal.PtrToStructure(ptr, typeof(Status));
            Marshal.FreeHGlobal(ptr);

            return config;
        }

        public Config ReadConfig()
        {
            var size = Marshal.SizeOf(typeof(Config));
            var configBytes = _process.ReadBytes(_ptrConfig.Value, size);
            var ptr = Marshal.AllocHGlobal(size);
            Marshal.Copy(configBytes, 0, ptr, size);
            var config = (Config)Marshal.PtrToStructure(ptr, typeof(Config));
            Marshal.FreeHGlobal(ptr);

            return config;
        }
        
        public byte[] ReadBytes(uint vptr, int size)
        {
            IntPtr ptr = new IntPtr((long)_ramPtrBase + (vptr & 0x7fffffff));
            return _process.ReadBytes(ptr, size);
        }
    }
}
