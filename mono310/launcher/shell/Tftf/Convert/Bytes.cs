// Newly authored. Little helpers for the converter: growable writer and bounds-checked reader.
using System;
using System.IO;
using System.Text;

namespace Tftf.Convert
{
    public sealed class ByteWriter
    {
        readonly MemoryStream _ms = new MemoryStream();
        public int Length { get { return (int)_ms.Length; } }
        public byte[] ToArray() { return _ms.ToArray(); }
        public void Bytes(byte[] b) { _ms.Write(b, 0, b.Length); }
        public void Bytes(byte[] b, int off, int len) { _ms.Write(b, off, len); }
        public void U8(int v) { _ms.WriteByte((byte)v); }
        public void I16(int v) { U8(v); U8(v >> 8); }
        public void I32(int v) { U8(v); U8(v >> 8); U8(v >> 16); U8(v >> 24); }
        public void I64(long v) { I32((int)v); I32((int)(v >> 32)); }
        public void U32BE(uint v) { U8((int)(v >> 24)); U8((int)(v >> 16)); U8((int)(v >> 8)); U8((int)v); }
        public void I64BE(long v) { U32BE((uint)(v >> 32)); U32BE((uint)v); }
        public void U16BE(int v) { U8(v >> 8); U8(v); }
        public void CStr(string s) { Bytes(Encoding.UTF8.GetBytes(s)); U8(0); }
        public void Align(int n) { while (_ms.Length % n != 0) U8(0); }
        /// <summary>Unity string: int32 length, bytes, padding to 4.</summary>
        public void AlignedString(string s) { var b = Encoding.UTF8.GetBytes(s); I32(b.Length); Bytes(b); Align(4); }
    }

    public sealed class ByteReader
    {
        public readonly byte[] Data;
        public int Pos;
        public ByteReader(byte[] data, int pos = 0) { Data = data; Pos = pos; }
        void Need(int n) { if (Pos < 0 || Pos + n > Data.Length) throw new EndOfStreamException("read past end at " + Pos + "+" + n); }
        public int U8() { Need(1); return Data[Pos++]; }
        public int I16() { Need(2); int v = (short)(Data[Pos] | Data[Pos + 1] << 8); Pos += 2; return v; }
        public int U16() { return I16() & 0xFFFF; }
        public int I32() { Need(4); int v = Data[Pos] | Data[Pos + 1] << 8 | Data[Pos + 2] << 16 | Data[Pos + 3] << 24; Pos += 4; return v; }
        public uint U32() { return (uint)I32(); }
        public long I64() { long lo = U32(); long hi = I32(); return lo | hi << 32; }
        public uint U32BE() { Need(4); uint v = (uint)(Data[Pos] << 24 | Data[Pos + 1] << 16 | Data[Pos + 2] << 8 | Data[Pos + 3]); Pos += 4; return v; }
        public byte[] Bytes(int n) { Need(n); var b = new byte[n]; Buffer.BlockCopy(Data, Pos, b, 0, n); Pos += n; return b; }
        public void Align(int n) { while (Pos % n != 0) Pos++; }
        public string CStr()
        {
            int s = Pos;
            while (Data[Pos] != 0) Pos++;
            string r = Encoding.UTF8.GetString(Data, s, Pos - s);
            Pos++;
            return r;
        }
        public string AlignedString()
        {
            int n = I32();
            if (n < 0) throw new InvalidDataException("negative string length");
            Need(n);
            string s = Encoding.UTF8.GetString(Data, Pos, n);
            Pos += n;
            Align(4);
            return s;
        }
    }
}
