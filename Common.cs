using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using System.IO;
using System.Xml;
using System.Xml.XPath;

namespace Apple.CoreFoundation
{
	/*
	HEADER
		magic number ("bplist")
		file format version
		byte length of plist incl. header, an encoded int number object (as below) [v.2+ only]
		32-bit CRC (ISO/IEC 8802-3:1989) of plist bytes w/o CRC, encoded always as
				"0x12 0x__ 0x__ 0x__ 0x__", big-endian, may be 0 to indicate no CRC [v.2+ only]

	OBJECT TABLE
		variable-sized objects

		Object Formats (marker byte followed by additional info in some cases)
		null	0000 0000			// null object [v1+ only]
		bool	0000 1000			// false
		bool	0000 1001			// true
		url	0000 1100	string		// URL with no base URL, recursive encoding of URL string [v1+ only]
		url	0000 1101	base string	// URL with base URL, recursive encoding of base URL, then recursive encoding of URL string [v1+ only]
		uuid	0000 1110			// 16-byte UUID [v1+ only]
		fill	0000 1111			// fill byte
		int	0001 0nnn	...		// # of bytes is 2^nnn, big-endian bytes
		real	0010 0nnn	...		// # of bytes is 2^nnn, big-endian bytes
		date	0011 0011	...		// 8 byte float follows, big-endian bytes
		data	0100 nnnn	[int]	...	// nnnn is number of bytes unless 1111 then int count follows, followed by bytes
		string	0101 nnnn	[int]	...	// ASCII string, nnnn is # of chars, else 1111 then int count, then bytes
		string	0110 nnnn	[int]	...	// Unicode string, nnnn is # of chars, else 1111 then int count, then big-endian 2-byte uint16_t
			0111 xxxx			// unused
		uid	1000 nnnn	...		// nnnn+1 is # of bytes
			1001 xxxx			// unused
		array	1010 nnnn	[int]	objref*	// nnnn is count, unless '1111', then int count follows
		ordset	1011 nnnn	[int]	objref* // nnnn is count, unless '1111', then int count follows [v1+ only]
		set	1100 nnnn	[int]	objref* // nnnn is count, unless '1111', then int count follows [v1+ only]
		dict	1101 nnnn	[int]	keyref* objref*	// nnnn is count, unless '1111', then int count follows
			1110 xxxx			// unused
			1111 xxxx			// unused

	OFFSET TABLE
		list of ints, byte size of which is given in trailer
		-- these are the byte offsets into the file
		-- number of these is in the trailer

	TRAILER
		byte size of offset ints in offset table
		byte size of object refs in arrays and dicts
		number of offsets in offset table (also is number of objects)
		element # in offset table which is top level object
		offset table offset

	Version 1.5 binary plists do not use object references (uid),
	but instead inline the object serialization itself at that point.
	It also doesn't use an offset table or a trailer.  It does have
	an extended header, and the top-level object follows the header.

	*/

	public struct BinaryPlistTrailer
	{
		//byte unused[5];
		public byte sortVersion;
		public byte offsetIntSize;
		public byte objectRefSize;
		public long numObjects;
		public long topObject;
		public long offsetTableOffset;

		public const int Size = 32;
	}

	[Flags]
	public enum BinaryPlistMarker
	{
		Null = 0x00,
		False = 0x08,
		True = 0x09,
		URL = 0xC,
		BaseURL = 0xD,
		UUID = 0x0E,
		Fill = 0x0F,
		Int = 0x10,
		Real = 0x20,
		Date = 0x33,
		Data = 0x40,
		ASCIIString = 0x50,
		Unicode16String = 0x60,
		UID = 0x80,
		Array = 0xA0,
		Set = 0xC0,
		Dict = 0xD0
	}

	public static class ValueExtensions
	{
		private static readonly DateTime AbsoluteDate = new DateTime(2001, 1, 1);

		private static byte[] TrimHigh(byte[] data, int nbytes)
		{
			if (nbytes >= data.Length)
				return data;
			var bytes = new byte[nbytes];
			if (BitConverter.IsLittleEndian)
				Array.Copy(data, 0, bytes, 0, nbytes);
			else
				Array.Copy(data, data.Length - nbytes, bytes, 0, nbytes);
			return bytes;
		}

		private static byte[] ExtendHigh(byte[] data, ref int startIndex, int length, int nbytes)
		{
			if (nbytes <= length)
				return data;
			var bytes = new byte[nbytes];
			Array.Clear(bytes, 0, nbytes);
			if (BitConverter.IsLittleEndian)
				Array.Copy(data, startIndex, bytes, 0, length);
			else
				Array.Copy(data, startIndex, bytes, bytes.Length - length, length);
			startIndex = 0;
			return bytes;
		}

		public static byte[] ToByteArray(this long value)
		{
			return ToByteArray(value, BytesCount(value));
		}

		public static byte[] ToByteArray(this ulong value)
		{
			return ToByteArray(value, BytesCount(value));
		}

		public static byte[] ToByteArray(this long value, int nbytes)
		{
			return TrimHigh(BitConverter.GetBytes(value), nbytes);
		}


		public static byte[] ToByteArray(this ulong value, int nbytes)
		{
			return TrimHigh(BitConverter.GetBytes(value), nbytes);
		}

		public static byte[] ToByteArray(this BigInteger value, int nbytes)
		{
			int start = 0;
			var bytes = value.ToByteArray();
			if (bytes.Length > nbytes)
				throw new ArgumentOutOfRangeException("value");
			return ExtendHigh(bytes, ref start, bytes.Length, nbytes);
		}

		public static int BytesCount(this long value)
		{
			if (value < 0)
				return 8;
			else if (value <= 0xffL)
				return 1;
			else if (value <= 0xffffL)
				return 2;
			else if (value <= 0xffffffffL)
				return 4;
			else
				return 8;
		}

		public static int BytesCount(this ulong value)
		{
			if (value <= 0xffUL)
				return 1;
			else if (value <= 0xffffUL)
				return 2;
			else if (value <= 0xffffffffUL)
				return 4;
			else
				return 8;
		}

		public static long ToInt64(this byte[] value)
		{
			if (value == null)
				throw new ArgumentNullException("value");
			return ToInt64(value, 0, value.Length);
		}

		public static ulong ToUInt64(this byte[] value)
		{
			if (value == null)
				throw new ArgumentNullException("value");
			return ToUInt64(value, 0, value.Length);
		}

		public static long ToInt64(this byte[] value, int startIndex, int length)
		{
			if (value == null)
				throw new ArgumentNullException("value");
			if (sizeof(long) < length || 0 == length)
				throw new ArgumentOutOfRangeException("value");
			var bytes = ExtendHigh(value, ref startIndex, length, sizeof(long));
			return BitConverter.ToInt64(bytes, startIndex);
		}

		public static ulong ToUInt64(this byte[] value, int startIndex, int length)
		{
			if (value == null)
				throw new ArgumentNullException("value");
			if (sizeof(long) < length || 0 == length)
				throw new ArgumentOutOfRangeException("value");
			var bytes = ExtendHigh(value, ref startIndex, length, sizeof(long));
			return BitConverter.ToUInt64(bytes, startIndex);
		}


		public static double GetAbsoluteTime(this DateTime date)
		{
			return date.Subtract(AbsoluteDate).TotalSeconds;
		}

		public static DateTime FromAbsoluteTime(this double time)
		{
			return AbsoluteDate.AddSeconds(time);
		}

		public static IEnumerable<object> GetKeysAndValues<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> dict)
		{
			var dl = new List<object>();
			dl.AddRange(dict.Select(item => (object)item.Key));
			dl.AddRange(dict.Select(item => (object)item.Value));
			return dl;
		}
	}

	public class DataComparer: IEqualityComparer<byte[]>, IEqualityComparer<object>
	{
		[System.Runtime.InteropServices.DllImport("msvcrt.dll")]
		private static extern int memcmp(byte[] a, byte[] b, UIntPtr count);

		public static bool Equals(byte[] x, byte[] y)
		{
			if (x == null || y == null || x.Length != y.Length)
				return false;
			return memcmp(x, y, new UIntPtr((ulong)x.Length)) == 0;
		}

		public int GetHashCode(byte[] value)
		{
			int hash = value.Length.GetHashCode();
			//foreach (byte b in value)
			//	hash ^= b.GetHashCode();
			return hash;
		}

		bool IEqualityComparer<byte[]>.Equals(byte[] x, byte[] y)
		{
			return Equals(x, y);
		}

		int IEqualityComparer<object>.GetHashCode(object value)
		{
			if (value == null)
				return 0;
			if (value is byte[])
				return GetHashCode((byte[])value);
			return value.GetHashCode();
		}

		bool IEqualityComparer<object>.Equals(object x, object y)
		{
			if (x is byte[] && y is byte[])
				return Equals((byte[])x, (byte[])y);
			return Object.Equals(x, y);
		}
	}

	public class Dict : Dictionary<string, object>, IEnumerable<KeyValuePair<string, object>>
	{
		private string[] keys;

		public Dict(object[] keysValues)
			: base(keysValues.Length)
		{
			int cnt = keysValues.Length >> 1;
			keys = new string[cnt];
			for (int idx1 = 0, idx2 = cnt; idx1 < cnt; idx1++, idx2++)
			{
				string key = (string)keysValues[idx1];
				base.Add(key, keysValues[idx2]);
				keys[idx1] = key;
			}
		}

		IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
		{
			return keys.Select(key => new KeyValuePair<string, object>(key, base[key])).GetEnumerator();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return keys.GetEnumerator();
		}
	}

	public class Set: HashSet<object>, IEnumerable<object>
	{
		private object[] list;

		public IEnumerable<object> GetValues()
		{
			var sl = new List<object>(list.Length);
			sl.AddRange(list);
			return sl;
		}

		public Set(object[] values)
			: base(values)
		{
			list = values;
		}

		IEnumerator<object> IEnumerable<object>.GetEnumerator()
		{
			return list.AsEnumerable().GetEnumerator();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return list.GetEnumerator();
		}
	}

	public class URL
	{
		public string Value { get; private set; }
		public URL Base { get; private set; }

		public URL(string url, URL urlbase = null)
		{
			if (url == null)
				throw new ArgumentNullException("url");
			Value = url;
			Base = urlbase;
		}

		public override int GetHashCode()
		{
			int hash = Value.GetHashCode();
			if (Base != null)
				hash ^= base.GetHashCode();
			return hash;
		}

		public override bool Equals(object obj)
		{
			var urlbase = obj as URL;
			if (urlbase == null || urlbase.Value != Value)
				return false;
			if (Base == null)
			{
				return urlbase.Base == null;
			}
			else
			{
				return Base.Equals(urlbase.Base);
			}
		}
	}
}
