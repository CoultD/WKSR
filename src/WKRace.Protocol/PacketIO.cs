using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WKRace.Protocol
{
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint Bits;
    }

    public sealed class PacketWriter
    {
        private byte[] _data;
        private int _length;

        public PacketWriter(int capacity = 128)
        {
            _data = new byte[capacity];
        }

        public int Length { get { return _length; } }
        public byte[] Data { get { return _data; } }

        public void Reset()
        {
            _length = 0;
        }

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Buffer.BlockCopy(_data, 0, result, 0, _length);
            return result;
        }

        private void Ensure(int extra)
        {
            int needed = _length + extra;
            if (needed <= _data.Length) return;
            int size = _data.Length * 2;
            while (size < needed) size *= 2;
            Array.Resize(ref _data, size);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _data[_length++] = value;
        }

        public void WriteBool(bool value)
        {
            WriteByte(value ? (byte)1 : (byte)0);
        }

        public void WriteUInt16(ushort value)
        {
            Ensure(2);
            _data[_length++] = (byte)value;
            _data[_length++] = (byte)(value >> 8);
        }

        public void WriteUInt32(uint value)
        {
            Ensure(4);
            for (int i = 0; i < 4; i++) _data[_length++] = (byte)(value >> (8 * i));
        }

        public void WriteInt32(int value)
        {
            WriteUInt32(unchecked((uint)value));
        }

        public void WriteInt64(long value)
        {
            Ensure(8);
            ulong u = unchecked((ulong)value);
            for (int i = 0; i < 8; i++) _data[_length++] = (byte)(u >> (8 * i));
        }

        public void WriteFloat(float value)
        {
            FloatBits bits = default(FloatBits);
            bits.Float = value;
            WriteUInt32(bits.Bits);
        }

        public void WriteGuid(Guid value)
        {
            byte[] bytes = value.ToByteArray();
            Ensure(16);
            Buffer.BlockCopy(bytes, 0, _data, _length, 16);
            _length += 16;
        }

        public void WriteFixedBytes(byte[] value, int count)
        {
            if (value == null || value.Length != count) throw new ProtocolException("Fixed byte field has wrong length");
            Ensure(count);
            Buffer.BlockCopy(value, 0, _data, _length, count);
            _length += count;
        }

        public void WriteString(string value, int maxChars)
        {
            if (value == null) value = string.Empty;
            if (value.Length > maxChars) value = value.Substring(0, maxChars);
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt16((ushort)bytes.Length);
            Ensure(bytes.Length);
            Buffer.BlockCopy(bytes, 0, _data, _length, bytes.Length);
            _length += bytes.Length;
        }
    }

    public sealed class PacketReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;

        public PacketReader(byte[] data, int offset, int count)
        {
            if (data == null || offset < 0 || count < 0 || offset + count > data.Length)
                throw new ProtocolException("Invalid packet bounds");
            _data = data;
            _position = offset;
            _end = offset + count;
        }

        public int Remaining { get { return _end - _position; } }

        private void Need(int count)
        {
            if (count < 0 || _end - _position < count) throw new ProtocolException("Packet truncated");
        }

        public byte ReadByte()
        {
            Need(1);
            return _data[_position++];
        }

        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        public ushort ReadUInt16()
        {
            Need(2);
            ushort v = (ushort)(_data[_position] | (_data[_position + 1] << 8));
            _position += 2;
            return v;
        }

        public uint ReadUInt32()
        {
            Need(4);
            uint v = 0;
            for (int i = 0; i < 4; i++) v |= (uint)_data[_position + i] << (8 * i);
            _position += 4;
            return v;
        }

        public int ReadInt32()
        {
            return unchecked((int)ReadUInt32());
        }

        public long ReadInt64()
        {
            Need(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)_data[_position + i] << (8 * i);
            _position += 8;
            return unchecked((long)v);
        }

        public float ReadFloat()
        {
            FloatBits bits = default(FloatBits);
            bits.Bits = ReadUInt32();
            return bits.Float;
        }

        public float ReadFiniteFloat()
        {
            float v = ReadFloat();
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new ProtocolException("Non-finite float");
            return v;
        }

        public Guid ReadGuid()
        {
            Need(16);
            var bytes = new byte[16];
            Buffer.BlockCopy(_data, _position, bytes, 0, 16);
            _position += 16;
            return new Guid(bytes);
        }

        public byte[] ReadFixedBytes(int count)
        {
            Need(count);
            var bytes = new byte[count];
            Buffer.BlockCopy(_data, _position, bytes, 0, count);
            _position += count;
            return bytes;
        }

        public string ReadString(int maxChars)
        {
            int byteLength = ReadUInt16();
            if (byteLength > maxChars * 3) throw new ProtocolException("String too long");
            Need(byteLength);
            string s = Encoding.UTF8.GetString(_data, _position, byteLength);
            _position += byteLength;
            if (s.Length > maxChars) throw new ProtocolException("String too long");
            return s;
        }
    }
}
