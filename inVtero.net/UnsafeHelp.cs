﻿// Copyright(C) 2017 Shane Macaulay smacaulay@gmail.com
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or(at your option) any later version.
//
//This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.If not, see<http://www.gnu.org/licenses/>.


using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using static inVtero.net.MagicNumbers;

namespace inVtero.net
{
    public unsafe class UnsafeHelp : IDisposable
    {
        const int WORD_MOD_SIZE = 63;
        const int WORD_BIT_SHIFT = 6; // right shift 5 = /32, 4 = /16

        public MemoryMappedFile BitMap;
        public MemoryMappedViewAccessor BitMapView;
        public long BitmapLen;
        FileStream BackingStream;

        string BitmapBackingFile;
        bool DeleteOnExit;

        static byte[] ZeroBuff;
        static byte[] FFFBuff;

        static UnsafeHelp()
        {
            ZeroBuff = new byte[PAGE_SIZE];
            FFFBuff = new byte[PAGE_SIZE];
            #if !NETSTANDARD2_0
            fixed (byte* allSetBits = FFFBuff)
                SetMemory(allSetBits, 0xff, PAGE_SIZE);
            #endif
        }

        UnsafeHelp() {  }

        public UnsafeHelp Clone()
        {
            var rv = new UnsafeHelp();
            rv.BitmapBackingFile = BitmapBackingFile;
            rv.SetupHandles();
            return rv;
        }

        void SetupHandles()
        {
            try {
                BackingStream = new FileStream(BitmapBackingFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                BitMap = MemoryMappedFile.CreateFromFile(
                        BackingStream,
                        null,
                        0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            }
            catch (Exception ex) {
                throw new FileLoadException($"Unable to setup mapping for {BitmapBackingFile}", ex);
            }

            if (File.Exists(BitmapBackingFile) && BitMap == null)
                throw new FileLoadException($"Can not load bitmap from {BitmapBackingFile}");

            BitMapView = BitMap.CreateViewAccessor();
            BitmapLen = BitMapView.Capacity;
            GetBitmapHandle();
        }

        public UnsafeHelp(string BitmapFileName, long CountOfEntries = 0, bool ShareExistingFile = true, bool Tempoary = false)
        {
            long ByteSize = (CountOfEntries >> 3) + sizeof(long);
            BitmapBackingFile = BitmapFileName;

            if(ShareExistingFile && !Tempoary && !File.Exists(BitmapBackingFile))
            {
                throw new FileNotFoundException($"Can't find shared file mapping {BitmapBackingFile}");
            }

            if (!File.Exists(BitmapBackingFile) || Tempoary)
            {
                // get a new file name
                BitmapBackingFile = Path.GetTempFileName();
                DeleteOnExit = true;
            }
            if (!File.Exists(BitmapBackingFile) || DeleteOnExit)
            {
                using (var fs = File.OpenWrite(BitmapBackingFile))
                {
                    fs.Seek(ByteSize, SeekOrigin.Begin);
                    fs.WriteByte(0);
                }
            }

            SetupHandles();
        }

#if DEPRECATE_FAVOUR_CORE
        public UnsafeHelp(string BitmapFileName, long CountOfEntries = 0, bool Shared = false, bool Tempoary = false)
        {
            long ByteSize = (CountOfEntries >> MagicNumbers.BIT_SHIFT)+sizeof(long);
            BitmapBackingFile = BitmapFileName;

            if (!Shared && File.Exists(BitmapBackingFile) || Tempoary)
            {
                // get a new file name
                BitmapBackingFile = Path.GetTempFileName();
                DeleteOnExit = true;
            }

            if (!File.Exists(BitmapBackingFile))
            {
                using (var fs = File.OpenWrite(BitmapBackingFile))
                {
                    fs.Seek(ByteSize, SeekOrigin.Begin);
                    fs.WriteByte(0);
                }
            }
            try
            {
                BackingStream = new FileStream(BitmapBackingFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                BitMap = MemoryMappedFile.CreateFromFile(
                        BackingStream, 
                        null, 
                        0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            } catch (Exception ex) { 
                    throw new MemoryMapWindowFailedException($"Unable to setup mapping for {BitmapFileName}", ex);
            }

            if (File.Exists(BitmapFileName) && BitMap == null)
                throw new FileLoadException($"Can not load bitmap from {BitmapFileName}");

            BitMapView = BitMap.CreateViewAccessor();
            BitmapLen = BitMapView.Capacity;
            GetBitmapHandle();
        }
#endif

        public static unsafe void ReadBytes<T>(MemoryMappedViewAccessor view, long offset, ref T[] arr, int Count = 512)
        {
            //byte[] arr = new byte[num];
            var ptr = (byte*)0;

            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            var ip = new IntPtr(ptr);
            var iplong = ip.ToInt64() + offset;
            var ptr_off = new IntPtr(iplong);

            if(arr is long[])
                Marshal.Copy(ptr_off, arr as long[], 0, Count);
            else if (arr is byte[])
                Marshal.Copy(ptr_off, arr as byte[], 0, Count);
            else if (arr is char[])
                Marshal.Copy(ptr_off, arr as char[], 0, Count);

            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        ulong* lp = (ulong *)0;

        public unsafe void MemSetBitmap(int c)
        {
#if !NETSTANDARD2_0
            SetMemory(lp, 0, (ulong) BitmapLen);
#endif
        }

        public unsafe void GetBitmapHandle()
        {
            var ptr = (byte*)0;
            BitMapView.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            lp = (ulong *)ptr;
        }

        public unsafe void ReleaseBitmapHandle()
        {
            BitMapView.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        public unsafe bool GetBit(ulong bit)
        {
            ulong slot = lp[bit >> WORD_BIT_SHIFT];
            ulong bitMasked = (1UL << ((int)bit & WORD_MOD_SIZE));
            ulong slotBit = slot & bitMasked;
            return slotBit != 0;

        }
        public unsafe void SetBit(ulong bit)
        {
            lp[(bit >> WORD_BIT_SHIFT)] |= (1UL << ((int)bit & WORD_MOD_SIZE));
        }

        /// <summary>
        /// </summary>
        /// <param name="view"></param>
        /// <param name="ScanFor"></param>
        /// <param name="Count"></param>
        /// <returns></returns>
        public static unsafe List<long> ScanBytes(MemoryMappedViewAccessor view, int ScanFor, int Count = 512)
        {
            var rv = new List<long>();
            var ptr = (byte*)0;

            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

            var iptr = (int*)ptr;

            for (long i = 0; i < Count; i++)
                if (iptr[i] == ScanFor)
                    rv.Add(i*4);

            view.SafeMemoryMappedViewHandle.ReleasePointer();

            return rv;
        }
#if !NETSTANDARD2_0
        public static unsafe int IsFFFPage<T>(T[] input)
        {
            fixed (byte* allSetBits = FFFBuff)
            {
                if (input is byte[]) fixed (byte* bp = (input as byte[]))
                        return CompareMemory(allSetBits, bp, PAGE_SIZE);
                if (input is char[]) fixed (char* cp = (input as char[]))
                        return CompareMemory(allSetBits, cp, PAGE_SIZE);
                if (input is long[]) fixed (long* lp = (input as long[]))
                        return CompareMemory(allSetBits, lp, PAGE_SIZE);
            }
            return -1;
        }


        public static unsafe int IsZeroPage<T>(T[] input)
        {
            fixed (byte* ZeroBytes = ZeroBuff)
            {
                if (input is byte[]) fixed (byte* bp = (input as byte[]))
                        return CompareMemory(ZeroBytes, bp, PAGE_SIZE);
                if (input is char[]) fixed (char* cp = (input as char[]))
                        return CompareMemory(ZeroBytes, cp, PAGE_SIZE);
                if (input is long[]) fixed (long* lp = (input as long[]))
                        return CompareMemory(ZeroBytes, lp, PAGE_SIZE);
            }
            return -1;
        }

        [DllImport("msvcrt.dll", EntryPoint = "memset", CallingConvention = CallingConvention.Cdecl, SetLastError = false), SuppressUnmanagedCodeSecurity]
        public static unsafe extern void* SetMemory(void* dest, int c, ulong count);

        [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl, SetLastError = false), SuppressUnmanagedCodeSecurity]
        public static unsafe extern void* CopyMemory(void* dest, void* src, ulong count);

        [DllImport("msvcrt.dll", EntryPoint = "memcmp", CallingConvention = CallingConvention.Cdecl, SetLastError = false), SuppressUnmanagedCodeSecurity]
        public static unsafe extern int CompareMemory(void* s1, void* s2, ulong count);
#endif

        public static unsafe bool UnsafeCompare(byte[] a1, byte[] a2)
        {
            if (a1 == a2) return true;
            if (a1 == null || a2 == null || a1.Length != a2.Length)
                return false;
            fixed (byte* p1 = a1, p2 = a2)
            {
                byte* x1 = p1, x2 = p2;
                int l = a1.Length;
                for (int i = 0; i < l / 8; i++, x1 += 8, x2 += 8)
                    if (*((long*)x1) != *((long*)x2)) return false;
                if ((l & 4) != 0) { if (*((int*)x1) != *((int*)x2)) return false; x1 += 4; x2 += 4; }
                if ((l & 2) != 0) { if (*((short*)x1) != *((short*)x2)) return false; x1 += 2; x2 += 2; }
                if ((l & 1) != 0) if (*((byte*)x1) != *((byte*)x2)) return false;
                return true;
            }
        }

        public static unsafe bool EqualBytesLongUnrolled(long[] data1, long[] data2, int offset = 0, int maxlen = 0)
        {
            if (data1 == data2)
                return true;

            if (data1.Length != data2.Length)
                return false;

            fixed (long* bytes1 = data1, bytes2 = data2)
            {
                var len = 0;

                if (maxlen != 0 && maxlen < (data1.Length - offset))
                    len = maxlen - offset;
                else
                    len = data1.Length - offset;

                int rem = len % (sizeof(long) * 16);
                long* b1 = (long*)bytes1 + offset;
                long* b2 = (long*)bytes2 + offset;
                long* e1 = (long*)(bytes1 + offset + len - rem);

                while (b1 < e1)
                {
                    if (*(b1) != *(b2) || *(b1 + 1) != *(b2 + 1) ||
                        *(b1 + 2) != *(b2 + 2) || *(b1 + 3) != *(b2 + 3) ||
                        *(b1 + 4) != *(b2 + 4) || *(b1 + 5) != *(b2 + 5) ||
                        *(b1 + 6) != *(b2 + 6) || *(b1 + 7) != *(b2 + 7) ||
                        *(b1 + 8) != *(b2 + 8) || *(b1 + 9) != *(b2 + 9) ||
                        *(b1 + 10) != *(b2 + 10) || *(b1 + 11) != *(b2 + 11) ||
                        *(b1 + 12) != *(b2 + 12) || *(b1 + 13) != *(b2 + 13) ||
                        *(b1 + 14) != *(b2 + 14) || *(b1 + 15) != *(b2 + 15))
                        return false;
                    b1 += 16;
                    b2 += 16;
                }

                for (int i = 0; i < rem; i++)
                    if (data1[len - 1 - i] != data2[len - 1 - i])
                        return false;

                return true;
            }
        }
        public static unsafe bool IsZero(byte[] data, int offset = 0, int count = 4096)
        {
            fixed (byte* bytes = data)
            {
                int rem = count % (sizeof(byte) * 16);
                long* b = (long*)bytes + offset;
                long* e = (long*)(bytes + count - rem);

                while (b < e)
                {
                    if ((*(b) | *(b + 1) | *(b + 2) | *(b + 3) | *(b + 4) |
                        *(b + 5) | *(b + 6) | *(b + 7) | *(b + 8) |
                        *(b + 9) | *(b + 10) | *(b + 11) | *(b + 12) |
                        *(b + 13) | *(b + 14) | *(b + 15)) != 0)
                        return false;
                    b += 16;
                }

                for (int i = 0; i < rem; i++)
                    if (data[count - 1 - i] != 0)
                        return false;

                return true;
            }
        }
        public static unsafe bool IsZero(long[] data, int offset = 0, int count = 512)
        {
            fixed (long* bytes = data)
            {
                int rem = count % (sizeof(long) * 16);
                long* b = (long*)bytes + offset;
                long* e = (long*)(bytes + count - rem);

                while (b < e)
                {
                    if ((*(b) | *(b + 1) | *(b + 2) | *(b + 3) | *(b + 4) |
                        *(b + 5) | *(b + 6) | *(b + 7) | *(b + 8) |
                        *(b + 9) | *(b + 10) | *(b + 11) | *(b + 12) |
                        *(b + 13) | *(b + 14) | *(b + 15)) != 0)
                        return false;
                    b += 16;
                }

                for (int i = 0; i < rem; i++)
                    if (data[count - 1 - i] != 0)
                        return false;

                return true;
            }
        }
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
#if !NETSTANDARD2_0
        private const uint FILE_READ_EA = 0x0008;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x2000000;

        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern uint GetFinalPathNameByHandle(IntPtr hFile, [MarshalAs(UnmanagedType.LPTStr)] StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(
                [MarshalAs(UnmanagedType.LPTStr)] string filename,
                [MarshalAs(UnmanagedType.U4)] uint access,
                [MarshalAs(UnmanagedType.U4)] FileShare share,
                IntPtr securityAttributes, // optional SECURITY_ATTRIBUTES struct or IntPtr.Zero
                [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
                [MarshalAs(UnmanagedType.U4)] uint flagsAndAttributes,
                IntPtr templateFile);

        public static string GetFinalPathName(string path)
        {
            var h = CreateFile(path,
                FILE_READ_EA,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
                throw new Win32Exception();

            try
            {
                var sb = new StringBuilder(1024);
                var res = GetFinalPathNameByHandle(h, sb, 1024, 0);
                if (res == 0)
                    throw new Win32Exception();

                return sb.ToString();
            }
            finally
            {
                CloseHandle(h);
            }
        }
#endif

#region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    ReleaseBitmapHandle();
                    BitMapView.Dispose();
                    BitMap.Dispose();

                    BackingStream.Dispose();
                    if (DeleteOnExit)
                        File.Delete(BitmapBackingFile);
                }
                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }
        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
#endregion

    }
}
