using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Numerics;

namespace Apple.CoreFoundation
{
	internal class BinReaderException: Exception
	{
		public BinReaderException(string path, BinaryPlistMarker marker, Exception ex) : base(ex.Message, ex)
		{
			Path = path;
			Marker = marker.ToString();
		}

		public BinReaderException(string path, string marker, Exception ex) : base(ex.Message, ex)
		{
			Path = path;
			Marker = marker;
		}

		public string Path { get; private set; }
		public string Marker { get; private set; }

		public override string Message
		{
			get
			{
				return String.Format("{0}\nat {1} ({2}):", base.Message, Path, Marker);
			}
		}
	}

	internal static class BinReaderExtensions
	{
		[System.Diagnostics.Conditional("DEBUG")]
		private static void Trace(string path)
		{
			Console.WriteLine(path);
		}

		internal static byte[] ReadBEBytes(this BinaryReader reader, int count)
		{
			var bytes = reader.ReadBytes(count);
			if (!BitConverter.IsLittleEndian)
				return bytes;
			var value = new byte[bytes.Length];
			for (int k1 = 0, k2 = bytes.Length - 1; k2 >= 0; k1++, k2--)
				value[k1] = bytes[k2];
			return value;
		}

		internal static string ReadBEString(this BinaryReader reader, int count)
		{
			var bytes = reader.ReadBytes(count * 2);
			if (!BitConverter.IsLittleEndian)
				return Encoding.Unicode.GetString(bytes);
			var data = new byte[bytes.Length];
			for (int k = 0; k < data.Length; k += 2)
			{
				data[k] = bytes[k + 1];
				data[k + 1] = bytes[k];
			}
			return Encoding.Unicode.GetString(data);
		}

		internal static int ReadIntProperty(this BinaryReader reader)
		{
			byte marker = reader.ReadByte();
			if ((marker & 0xf0) != (int)BinaryPlistMarker.Int)
				throw new InvalidDataException("Unknown marker " + marker);
			return (int)reader.ReadBEInt(marker);
		}

		internal static long ReadBEInt(this BinaryReader reader, byte marker)
		{
			int cnt = 1 << (marker & 0x0f);
			var bytes = reader.ReadBEBytes(cnt);
			// in format version '00' and '15', 1, 2, and 4-byte integers have to be interpreted as unsigned,
			// whereas 8-byte integers are signed (and 16-byte when available)
			// negative 1, 2, 4-byte integers are always emitted as 8 bytes in format '00' and '15'
			// integers are not required to be in the most compact possible representation, but only the last 64 bits are significant currently
			long bigint = bytes.ToInt64();
			return bigint;
		}

		internal static ulong ReadBEUID(this BinaryReader reader, byte marker)
		{
			int cnt = (marker & 0x0f) + 1;
			var bytes = reader.ReadBEBytes(cnt);
			// uids are not required to be in the most compact possible representation, but only the last 64 bits are significant currently
			ulong uid = bytes.ToUInt64();
			return uid;
		}

		internal static BigInteger ReadBEInt128(this BinaryReader reader)
		{
			var bytes = reader.ReadBEBytes(16);
			return new BigInteger(bytes);
		}

		internal static float ReadBEFloat(this BinaryReader reader)
		{
			var bytes = reader.ReadBEBytes(sizeof(float));
			float f = BitConverter.ToSingle(bytes, 0);
			return f;
		}

		internal static double ReadBEDouble(this BinaryReader reader)
		{
			var bytes = reader.ReadBEBytes(sizeof(double));
			double d = BitConverter.ToDouble(bytes, 0);
			return d;
		}

		internal static DateTime ReadBEDateTime(this BinaryReader reader)
		{
			var bytes = reader.ReadBEBytes(sizeof(double));
			double d = BitConverter.ToDouble(bytes, 0);
			return d.FromAbsoluteTime();
		}

		internal static byte[] ReadData(this BinaryReader reader, byte marker)
		{
			int cnt = marker & 0x0f;
			if (0xf == cnt)
			{
				cnt = reader.ReadIntProperty();
			}
			return reader.ReadBytes(cnt);
		}

		internal static string ReadASCIIString(this BinaryReader reader, byte marker)
		{
			int cnt = marker & 0x0f;
			if (0xf == cnt)
			{
				cnt = reader.ReadIntProperty();
			}
			var bytes = reader.ReadBytes(cnt);
			return Encoding.ASCII.GetString(bytes);
		}

		internal static string ReadUnicodeString(this BinaryReader reader, byte marker)
		{
			int cnt = marker & 0x0f;
			if (0xf == cnt)
			{
				cnt = (int)reader.ReadIntProperty();
			}
			return reader.ReadBEString(cnt);
		}

		private static long GetOffsetOfRefAt(BinaryReader reader, ref BinaryPlistTrailer trailer)
		{
			// *trailer contents are trusted, even for overflows -- was checked when the trailer was parsed;
			// this pointer arithmetic and the multiplication was also already done once and checked,
			// and the offsetTable was already validated.
			long pos = reader.BaseStream.Position;
			long objectsFirstBytePos = 8;
			if (pos < objectsFirstBytePos || trailer.offsetTableOffset - trailer.objectRefSize < pos)
				throw new IndexOutOfRangeException("Object offset is out of stream");
			var bytes = reader.ReadBEBytes(trailer.objectRefSize);
			long refnum = bytes.ToInt64();
			if (trailer.numObjects <= refnum)
				throw new IndexOutOfRangeException("Object number is out of range");

			pos = reader.BaseStream.Position;
			long offPos = trailer.offsetTableOffset + refnum * trailer.offsetIntSize;
			reader.BaseStream.Seek(offPos, SeekOrigin.Begin);
			bytes = reader.ReadBEBytes(trailer.offsetIntSize);
			long off = bytes.ToInt64();
			reader.BaseStream.Seek(pos, SeekOrigin.Begin);
			return off;
		}

		private static object[] ReadArray(BinaryReader reader, byte marker, ref BinaryPlistTrailer trailer, IDictionary<long, object> objects, Func<object[], int, string> child, ISet<long> set = null)
		{
			bool v15 = trailer.offsetIntSize == 0 || trailer.objectRefSize == 0;
			int cnt = marker & 0x0f;
			if (0xf == cnt)
			{
				cnt = (int)reader.ReadIntProperty();
			}
			bool isDict = (marker & 0xf0) == (int)BinaryPlistMarker.Dict;
			int kcnt = cnt;
			if (isDict)
				cnt = cnt * 2;
			var list = new object[cnt];
			for (int idx = 0; idx < cnt; idx++)
			{
				string path = child(list, idx);
				if (!v15)
				{
					long startOffset;
					try
					{
						startOffset = GetOffsetOfRefAt(reader, ref trailer);
						// databytes is trusted to be at least datalen bytes long
						// *trailer contents are trusted, even for overflows -- was checked when the trailer was parsed
						long objectsRangeStart = 8, objectsRangeEnd = trailer.offsetTableOffset - 1;
						if (startOffset < objectsRangeStart || objectsRangeEnd < startOffset)
							throw new IndexOutOfRangeException("Offset is out of stream");
						// at any one invocation of this function, set should contain the offsets in the "path" down to this object
						if (set != null)
						{
							if (set.Contains(startOffset))
								throw new InvalidDataException("Not unique set");
							set.Add(startOffset);
						}
					}
					catch (Exception ex)
					{
						throw new BinReaderException(path, "ObjRef", ex);
					}
					finally
					{
						Trace(path + "#");
					}

					long pos = reader.BaseStream.Position;
					reader.BaseStream.Seek(startOffset, SeekOrigin.Begin);
					list[idx] = reader.ReadObject(ref trailer, objects, path);
					reader.BaseStream.Seek(pos, SeekOrigin.Begin);
				}
				else
				{
					list[idx] = reader.ReadObject(ref trailer, objects, path);
				}
			}
			return list;
		}

		internal static IDictionary<string, object> ReadBEDict(this BinaryReader reader, byte marker, ref BinaryPlistTrailer trailer, IDictionary<long, object> objects, string path)
		{
			var keysValues = ReadArray(reader, marker, ref trailer, objects, (list, idx) =>
				idx * 2 >= list.Length ? String.Format("{0}/dict[{1}]", path, list[idx - list.Length / 2]) : String.Format("{0}/dict.key[{1}]", path, idx));
			return new Dict(keysValues);
		}

		internal static object[] ReadBEArray(this BinaryReader reader, byte marker, ref BinaryPlistTrailer trailer, IDictionary<long, object> objects, string path)
		{
			var values = ReadArray(reader, marker, ref trailer, objects, (list, idx) => String.Format("{0}/array[{1}]", path, idx));
			return values;
		}

		internal static ISet<object> ReadBESet(this BinaryReader reader, byte marker, ref BinaryPlistTrailer trailer, IDictionary<long, object> objects, string path)
		{
			var values = ReadArray(reader, marker, ref trailer, objects, (list, idx) => String.Format("{0}/set[{1}]", path, idx), new HashSet<long>());
			return new Set(values);
		}

		internal static URL ReadURL(this BinaryReader reader)
		{
			byte marker = reader.ReadByte();
			string url;
			switch((BinaryPlistMarker)(marker & 0xf0))
			{
			case BinaryPlistMarker.ASCIIString:
				url = reader.ReadASCIIString(marker);
				break;
			case BinaryPlistMarker.Unicode16String:
				url = reader.ReadUnicodeString(marker);
				break;
			default:
				throw new InvalidDataException("Unknown URL marker " + marker);
			}
			return new URL(url);
		}

		internal static URL ReadBaseURL(this BinaryReader reader)
		{
			byte marker = reader.ReadByte();
			URL urlbase;
			switch ((BinaryPlistMarker)marker)
			{
			case BinaryPlistMarker.URL:
				urlbase = reader.ReadURL();
				break;
			case BinaryPlistMarker.BaseURL:
				urlbase = reader.ReadBaseURL();
				break;
			default:
				throw new InvalidDataException("Unknown base URL marker " + marker);
			}
			string url = reader.ReadURL().Value;
			return new URL(url, urlbase);
		}

		internal static Guid ReadUUID(this BinaryReader reader)
		{
			var bytes = reader.ReadBytes(16);
			return new Guid(bytes);
		}

		internal static object ReadObject(this BinaryReader reader, string path)
		{
			var trailer = new BinaryPlistTrailer
			{
				offsetIntSize = 0,
				objectRefSize = 0
			};
			return reader.ReadObject(ref trailer, null, path);
		}

		internal static object ReadObject(this BinaryReader reader, ref BinaryPlistTrailer trailer, IDictionary<long, object> objects, string path)
		{
			bool v15 = trailer.offsetIntSize == 0 || trailer.objectRefSize == 0;
			object plist;
			long startOffset = reader.BaseStream.Position;
			if (objects != null && objects.TryGetValue(startOffset, out plist))
				return plist;

			byte marker = reader.ReadByte();
			var type = (BinaryPlistMarker)(marker & 0xf0);
			try
			{
				switch (type)
				{
				case BinaryPlistMarker.Null:
					type = (BinaryPlistMarker)marker;
					switch (type)
					{
					case BinaryPlistMarker.Null:
						return null;
					case BinaryPlistMarker.False:
						return false;
					case BinaryPlistMarker.True:
						return true;
					case BinaryPlistMarker.URL:
						if (v15)
							return reader.ReadURL();
						break;
					case BinaryPlistMarker.BaseURL:
						if (v15)
							return reader.ReadBaseURL();
						break;
					case BinaryPlistMarker.UUID:
						if (v15)
							return reader.ReadUUID();
						break;
					case BinaryPlistMarker.Fill:
						break;
					}
					throw new InvalidDataException("Unknown marker " + marker);
				case BinaryPlistMarker.Int:
					if (marker == ((byte)BinaryPlistMarker.Int | 4))
						plist = reader.ReadBEInt128();
					else
						plist = reader.ReadBEInt(marker);
					// these are always immutable
					if (objects != null)
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.Real:
					switch (marker & 0x0f)
					{
					case 2:
						plist = reader.ReadBEFloat();
						break;
					case 3:
						plist = reader.ReadBEDouble();
						break;
					default:
						throw new InvalidDataException("Unknown marker " + marker);
					}

					// these are always immutable
					if (objects != null)
						objects[startOffset] = plist;
					return plist;
				case (BinaryPlistMarker)((int)BinaryPlistMarker.Date & 0xf0):
					type = (BinaryPlistMarker)marker;
					if (type != BinaryPlistMarker.Date)
						throw new InvalidDataException("Unknown marker " + marker);
					plist = reader.ReadBEDateTime();
					// these are always immutable
					if (objects != null)
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.Data:
					plist = reader.ReadData(marker);
					if (objects != null) //&& mutabilityOption != kCFPropertyListMutableContainersAndLeaves
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.ASCIIString:
					plist = reader.ReadASCIIString(marker);
					if (objects != null) //&& mutabilityOption != kCFPropertyListMutableContainersAndLeaves
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.Unicode16String:
					plist = reader.ReadUnicodeString(marker);
					if (objects != null) //&& mutabilityOption != kCFPropertyListMutableContainersAndLeaves
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.UID:
					if (v15)
						throw new InvalidDataException("Unknown marker " + marker);
					plist = reader.ReadBEUID(marker);
					// these are always immutable
					if (objects != null)
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.Dict:
					plist = reader.ReadBEDict(marker, ref trailer, objects, path);
					if (objects != null) //&& mutabilityOption == kCFPropertyListImmutable
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.Array:
					plist = reader.ReadBEArray(marker, ref trailer, objects, path);
					if (objects != null) //&& mutabilityOption == kCFPropertyListImmutable
						objects[startOffset] = plist;
					return plist;
				case BinaryPlistMarker.Set:
					if (!v15)
						throw new InvalidDataException("Unknown marker " + marker);
					plist = reader.ReadBESet(marker, ref trailer, objects, path);
					if (objects != null) //&& mutabilityOption == kCFPropertyListImmutable
						objects[startOffset] = plist;
					return plist;
				default:
					throw new InvalidDataException("Unknown marker " + marker);
				}
			}
			catch (BinReaderException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new BinReaderException(path, type, ex);
			}
			finally
			{
				Trace(path);
			}
		}

		internal static BinaryPlistTrailer ReadTrailer(this BinaryReader reader)
		{
			try
			{
				var trailer = new BinaryPlistTrailer();
				// In Leopard, the unused bytes in the trailer must be 0 or the parse will fail
				// This check is not present in Tiger and earlier or after Leopard
				reader.ReadBytes(5); //unused[5]
				trailer.sortVersion = reader.ReadByte();
				trailer.offsetIntSize = reader.ReadByte();
				trailer.objectRefSize = reader.ReadByte();
				trailer.numObjects = BitConverter.ToInt64(reader.ReadBEBytes(sizeof(Int64)), 0);
				trailer.topObject = BitConverter.ToInt64(reader.ReadBEBytes(sizeof(Int64)), 0);
				trailer.offsetTableOffset = BitConverter.ToInt64(reader.ReadBEBytes(sizeof(Int64)), 0);
				return trailer;
			}
			catch (Exception ex)
			{
				throw new BinReaderException("trailer", BinaryPlistMarker.Data, ex);
			}
		}
	}

	public static class Binary2PropertyList
	{
		private static bool BinaryPlistGetTopLevelInfo(Stream stream, out long offset, out BinaryPlistTrailer trailer, out string version)
		{
			if (stream == null)
				throw new ArgumentNullException("stream");
			if (stream.Length < BinaryPlistTrailer.Size + 8 + 1)
				throw new ArgumentOutOfRangeException("stream");
			// Tiger and earlier will parse "bplist00"
			// Leopard will parse "bplist00" or "bplist01"
			// SnowLeopard will parse "bplist0?" where ? is any one character
			var reader = new BinaryReader(stream);
			stream.Seek(0, SeekOrigin.Begin);
			string header = Encoding.ASCII.GetString(reader.ReadBytes(8));
			if (!header.StartsWith("bplist"))
				throw new NotSupportedException("Invalid header");
			version = String.Format("{0}.{1}", header[6], header[7]);
			if (header[6] != '0')
			{
				offset = 0;
				trailer = new BinaryPlistTrailer();
				return false;
			}
			stream.Seek(-BinaryPlistTrailer.Size, SeekOrigin.End);
			trailer = reader.ReadTrailer();

			// Don't overflow on the number of objects or offset of the table
			// The trailer must point to a value before itself in the data.
			if (stream.Length < trailer.numObjects ||
				stream.Length < trailer.offsetTableOffset ||
				stream.Length - BinaryPlistTrailer.Size <= trailer.offsetTableOffset)
				throw new IndexOutOfRangeException("Trailer is out of stream");

			//  Must be a minimum of 1 object
			if (trailer.numObjects < 1)
				throw new InvalidDataException("Objects not found");

			// The ref to the top object must be a value in the range of 1 to the total number of objects
			if (trailer.numObjects <= trailer.topObject)
				throw new IndexOutOfRangeException("Invalid top object ref");

			// The offset table must be after at least 9 bytes of other data ('bplist??' + 1 byte of object table data).
			if (trailer.offsetTableOffset < 9)
				throw new InvalidDataException("Invalid offset table offset");

			// Minimum of 1 byte for the size of integers and references in the data
			if (trailer.offsetIntSize < 1)
				throw new InvalidDataException("Invalid size of integers");
			if (trailer.objectRefSize < 1)
				throw new InvalidDataException("Invalid size of references");

			// The total size of the offset table (number of objects * size of each int in the table) must not overflow
			long offsetIntSize = trailer.offsetIntSize;
			long offsetTableSize = trailer.numObjects * offsetIntSize;
			if (stream.Length < offsetTableSize)
				throw new IndexOutOfRangeException("Offset table is out of stream");

			// The offset table must have at least 1 entry
			if (offsetTableSize < 1)
				throw new InvalidDataException("Offsets not found");

			// Make sure the size of the offset table and data sections do not overflow
			long objectDataSize = trailer.offsetTableOffset - 8;
			long tmpSum = 8 + objectDataSize + offsetTableSize + BinaryPlistTrailer.Size;

			// The total size of the data should be equal to the sum of offsetTableOffset + sizeof(trailer)
			if (stream.Length != tmpSum)
				throw new InvalidDataException("Invalid total data size");

			// The object refs must be the right size to point into the offset table. That is, if the count of objects is 260, but only 1 byte is used to store references (max value 255), something is wrong.
			if (trailer.objectRefSize < 8 && (1L << (8 * trailer.objectRefSize)) <= trailer.numObjects)
				throw new InvalidDataException("Invalid object refs size");

			// The integers used for pointers in the offset table must be able to reach as far as the start of the offset table.
			if (trailer.offsetIntSize < 8 && (1L << (8 * trailer.offsetIntSize)) <= trailer.offsetTableOffset)
				throw new InvalidDataException("Invalid offset table size");

			long offsetsFirstBytePos = trailer.offsetTableOffset;
			long offsetsLastBytePos = offsetsFirstBytePos + offsetTableSize - 1;

			stream.Seek(trailer.offsetTableOffset, SeekOrigin.Begin);
			long maxOffset = trailer.offsetTableOffset - 1;
			for (int idx = 0; idx < trailer.numObjects; idx++)
			{
				long off = reader.ReadBEBytes(trailer.offsetIntSize).ToInt64();
				if (maxOffset < off)
					throw new IndexOutOfRangeException("Invalid offset #" + idx);
			}

			stream.Seek(trailer.offsetTableOffset + trailer.topObject * trailer.offsetIntSize, SeekOrigin.Begin);
			offset = reader.ReadBEBytes(trailer.offsetIntSize).ToInt64();
			if (offset < 8 || trailer.offsetTableOffset <= offset)
				throw new IndexOutOfRangeException("Invalid top object offset");

			stream.Seek(offset, SeekOrigin.Begin);
			return true;
		}

		private static bool BinaryPlistGetTopLevelInfo15(Stream stream, out string version)
		{
			if (stream == null)
				throw new ArgumentNullException("stream");
			if (23 <= stream.Length)
				throw new ArgumentOutOfRangeException("stream");
			var reader = new BinaryReader(stream);
			stream.Seek(0, SeekOrigin.Begin);
			string header = Encoding.ASCII.GetString(reader.ReadBytes(8));
			if (!header.StartsWith("bplist"))
				throw new NotSupportedException("Invalid header");
			version = String.Format("{0}.{1}", header[6], header[7]);
			if (header.Substring(6) != "15")
				return false;
			byte marker = reader.ReadByte();
			if (marker != ((int)BinaryPlistMarker.Int | 3))
				throw new InvalidDataException("Invalid header v1.5");
			long bytelen = reader.ReadBEInt(marker);
			if (bytelen != stream.Length)
				throw new InvalidDataException("Invalid header v1.5 byte length");
			marker = reader.ReadByte();
			if (marker != ((int)BinaryPlistMarker.Int | 2))
				throw new InvalidDataException("Invalid header v1.5 crc format");
			reader.ReadBEInt(marker);
			return true;
		}

		private static object BinaryPlistCreateObject(Stream stream, ref BinaryPlistTrailer trailer, string version)
		{
			var reader = new BinaryReader(stream);
			return reader.ReadObject(ref trailer, new Dictionary<long, object>(), String.Format("/plist[{0}]", version));
		}

		private static object BinaryPlistCreateObject15(Stream stream)
		{
			var reader = new BinaryReader(stream);
			return reader.ReadObject("/plist[1.5]");
		}

		public static object BinaryPlistCreate00(Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException("stream");
			BinaryPlistTrailer trailer;
			long offset;
			string ver;

			if (BinaryPlistGetTopLevelInfo(stream, out offset, out trailer, out ver))
			{
				return BinaryPlistCreateObject(stream, ref trailer, ver);
			}
			return null;
		}

		public static object BinaryPlistCreate15(Stream stream)
		{
			string ver;

			if (BinaryPlistGetTopLevelInfo15(stream, out ver))
			{
				return BinaryPlistCreateObject15(stream);
				// ptr should equal (databytes+datalen) if there is no junk at the end of top-level object
			}
			return null;
		}

		public static object BinaryPlistCreate(Stream stream, out string version)
		{
			BinaryPlistTrailer trailer;
			long offset;

			if (BinaryPlistGetTopLevelInfo(stream, out offset, out trailer, out version))
			{
				return BinaryPlistCreateObject(stream, ref trailer, version);
			}
			else if (BinaryPlistGetTopLevelInfo15(stream, out version))
			{
				return BinaryPlistCreateObject15(stream);
			}
			throw new NotSupportedException("Unsupported version " + version);
		}

		public static object BinaryPlistCreate(Stream stream)
		{
			string version;
			return BinaryPlistCreate(stream, out version);
		}
	}
}
