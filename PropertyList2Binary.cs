using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Numerics;

namespace Apple.CoreFoundation
{
	internal class BinWriterException: Exception
	{
		public BinWriterException(string path, Type type, Exception ex) : base(ex.Message, ex)
		{
			Path = path;
			Type = type;
		}

		public string Path { get; private set; }
		public Type Type { get; private set; }

		public override string Message
		{
			get
			{
				string msg = String.Format("{0} ({1}):", Path, Type);
				msg += Environment.NewLine;
				msg += base.Message;
				return msg;
			}
		}
	}

	internal static class BinWriterExtensions
	{
		private static Encoding ascii;

		internal static Encoding ASCII
		{
			get
			{
				if (ascii == null)
				{
					ascii = (Encoding)Encoding.ASCII.Clone();
					ascii.EncoderFallback = new EncoderReplacementFallback(String.Empty);
				}
				return ascii;
			}
		}

		[System.Diagnostics.Conditional("DEBUG")]
		private static void Trace(string path)
		{
			Console.WriteLine(path);
		}

		internal static void WriteBE(this BinaryWriter writer, byte[] value)
		{
			if (BitConverter.IsLittleEndian)
			{
				var bytes = new byte[value.Length];
				for (int k1 = 0, k2 = value.Length - 1; k2 >= 0; k1++, k2--)
					bytes[k1] = value[k2];
				writer.Write(bytes);
			}
			else
			{
				writer.Write(value);
			}
		}

		internal static void WriteBE(this BinaryWriter writer, string value)
		{
			var data = Encoding.Unicode.GetBytes(value);
			if (BitConverter.IsLittleEndian)
			{
				var bytes = new byte[data.Length];
				for (int k = 0; k < bytes.Length; k += 2)
				{
					bytes[k] = data[k + 1];
					bytes[k + 1] = data[k];
				}
				writer.Write(bytes);
			}
			else
			{
				writer.Write(data);
			}
		}

		internal static void WriteProperty(this BinaryWriter writer, long bigint)
		{
			writer.WriteProperty(bigint, bigint.BytesCount());
		}

		internal static void WriteProperty(this BinaryWriter writer, long bigint, int nbytes)
		{
			byte marker = (byte)BinaryPlistMarker.Int;
			var bytes = bigint.ToByteArray(nbytes);
			switch (bytes.Length)
			{
			case 1:
				marker |= 0;
				break;
			case 2:
				marker |= 1;
				break;
			case 4:
				marker |= 2;
				break;
			default:
				marker |= 3;
				break;
			}
			writer.Write(marker);
			writer.WriteBE(bytes);
		}

		internal static void WriteProperty(this BinaryWriter writer, ulong uid)
		{
			byte marker = (byte)BinaryPlistMarker.UID;
			var bytes = uid.ToByteArray();
			marker |= (byte)(bytes.Length - 1);
			writer.Write(marker);
			writer.WriteBE(bytes);
		}

		internal static void WriteProperty(this BinaryWriter writer, string value)
		{
			int count = value.Length;
			var bytes = ASCII.GetBytes(value);
			byte marker = (byte)(count < 15 ? count : 0xf);
			if (bytes.Length == count)
			{
				marker |= (byte)BinaryPlistMarker.ASCIIString;
				writer.Write(marker);
				if (15 <= count)
				{
					writer.WriteProperty(count);
				}
				writer.Write(bytes);
			}
			else
			{
				marker |= (byte)BinaryPlistMarker.Unicode16String;
				writer.Write(marker);
				if (15 <= count)
				{
					writer.WriteProperty(count);
				}
				writer.WriteBE(value);
			}
		}

		internal static void WriteProperty(this BinaryWriter writer, float value)
		{
			byte marker = (byte)BinaryPlistMarker.Real | 2;
			writer.Write(marker);
			var bytes = BitConverter.GetBytes(value);
			writer.WriteBE(bytes);
		}

		internal static void WriteProperty(this BinaryWriter writer, double value)
		{
			byte marker = (byte)BinaryPlistMarker.Real | 3;
			writer.Write(marker);
			var bytes = BitConverter.GetBytes(value);
			writer.WriteBE(bytes);
		}

		internal static void WriteProperty(this BinaryWriter writer, BigInteger value)
		{
			byte marker = (byte)BinaryPlistMarker.Int | 4;
			writer.Write(marker);
			var bytes = value.ToByteArray(16);
			writer.WriteBE(bytes);
		}

		internal static void WriteProperty(this BinaryWriter writer, bool value)
		{
			byte marker = (byte)(value ? BinaryPlistMarker.True : BinaryPlistMarker.False);
			writer.Write(marker);
		}

		internal static void WriteProperty(this BinaryWriter writer, DateTime date)
		{
			byte marker = (byte)BinaryPlistMarker.Date;
			writer.Write(marker);
			var swapped = date.GetAbsoluteTime();
			var bytes = BitConverter.GetBytes(swapped);
			writer.WriteBE(bytes);
		}

		internal static void WriteNullProperty(this BinaryWriter writer)
		{
			byte marker = 0;
			writer.Write(marker);
		}

		internal static void WriteProperty(this BinaryWriter writer, byte[] value)
		{
			int count = value.Length;
			byte marker = (byte)((int)BinaryPlistMarker.Data | (count < 15 ? count : 0xf));
			writer.Write(marker);
			if (15 <= count)
			{
				writer.WriteProperty(count);
			}
			writer.Write(value);
		}

		private static void WriteArray(BinaryWriter writer, IEnumerable<object> list, IDictionary<object, int> objtable, int objRefSize, Func<int, string> child)
		{
			bool v15 = objtable == null || objRefSize == 0;
			int idx = 0;
			foreach (var value in list)
			{
				string path = child(idx++);
				if (!v15)
				{
					try
					{
						long refnum = objtable[value];
						var bytes = refnum.ToByteArray(objRefSize);
						writer.WriteBE(bytes);
					}
					catch (Exception ex)
					{
						throw new BinWriterException(path, objtable.GetType(), ex);
					}
					finally
					{
						Trace(path + "#");
					}
				}
				else
				{
					writer.WriteObject(value, path, objtable, objRefSize);
				}
			}
		}

		internal static void WriteProperty(this BinaryWriter writer, IDictionary<string, object> dict, string path, IDictionary<object, int> objtable = null, int objRefSize = 0)
		{
			int count = dict.Count;
			byte marker = (byte)((int)BinaryPlistMarker.Dict | (count < 15 ? count : 0xf));
			writer.Write(marker);
			if (15 <= count)
			{
				writer.WriteProperty(count);
			}
			var keys = dict.Select(item => item.Key).ToArray();
			WriteArray(writer, dict.GetKeysAndValues(), objtable, objRefSize, idx =>
				idx >= keys.Length ? String.Format("{0}/dict[{1}]", path, keys[idx - keys.Length]) : String.Format("{0}/dict.key[{1}]", path, idx));
		}

		internal static void WriteProperty(this BinaryWriter writer, object[] list, string path, IDictionary<object, int> objtable = null, int objRefSize = 0)
		{
			int count = list.Length;
			byte marker = (byte)((int)BinaryPlistMarker.Array | (count < 15 ? count : 0xf));
			writer.Write(marker);
			if (15 <= count)
			{
				writer.WriteProperty(count);
			}
			WriteArray(writer, list, objtable, objRefSize, idx => String.Format("{0}/array[{1}]", path, idx));
		}

		internal static void WriteProperty(this BinaryWriter writer, ISet<object> list, string path, IDictionary<object, int> objtable = null, int objRefSize = 0)
		{
			int count = list.Count;
			byte marker = (byte)((int)BinaryPlistMarker.Set | (count < 15 ? count : 0xf));
			writer.Write(marker);
			if (15 <= count)
			{
				writer.WriteProperty(count);
			}
			WriteArray(writer, list, objtable, objRefSize, idx => String.Format("{0}/set[{1}]", path, idx));
		}

		internal static void WriteProperty(this BinaryWriter writer, Guid uu)
		{
			byte marker = (byte)BinaryPlistMarker.UUID;
			writer.Write(marker);
			var bytes = uu.ToByteArray();
			writer.Write(bytes);
		}

		internal static void WriteProperty(this BinaryWriter writer, URL url)
		{
			byte marker = (byte)(url.Base != null ? BinaryPlistMarker.BaseURL : BinaryPlistMarker.URL);
			writer.Write(marker);
			if (url.Base != null)
				writer.WriteProperty(url.Base);
			writer.WriteProperty(url.Value);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="writer"></param>
		/// <param name="obj"></param>
		/// <param name="objtable"></param>
		/// <param name="objRefSize">0 == objRefSize means version 1.5, else version 0.0 or 0.1</param>
		/// <returns></returns>
		internal static void WriteObject(this BinaryWriter writer, object obj, string path, IDictionary<object, int> objtable = null, int objRefSize = 0)
		{
			Type type = obj == null ? null : obj.GetType();
			bool v15 = objRefSize == 0;
			try
			{
				if (obj is string)
					writer.WriteProperty((string)obj);
				else if (obj is float)
					writer.WriteProperty((float)obj);
				else if (obj is double)
					writer.WriteProperty((double)obj);
				else if (obj is BigInteger)
					writer.WriteProperty((BigInteger)obj);
				else if (obj is long || obj is int)
					writer.WriteProperty(Convert.ToInt64(obj));
				else if (obj is bool)
					writer.WriteProperty((bool)obj);
				else if (v15 && obj == null)
					writer.WriteNullProperty();
				else if (obj is byte[])
					writer.WriteProperty((byte[])obj);
				else if (obj is DateTime)
					writer.WriteProperty((DateTime)obj);
				else if (obj is IDictionary<string, object>)
					writer.WriteProperty((IDictionary<string, object>)obj, path, objtable, objRefSize);
				else if (obj is object[])
					writer.WriteProperty((object[])obj, path, objtable, objRefSize);
				else if (v15 && obj is ISet<object>)
					writer.WriteProperty((ISet<object>)obj, path, objtable, objRefSize);
				else if (v15 && obj is Guid)
					writer.WriteProperty((Guid)obj);
				else if (v15 && obj is URL)
					writer.WriteProperty((URL)obj);
				else if (!v15 && obj is ulong)
					writer.WriteProperty((ulong)obj);
				else
					throw new NotSupportedException("object type not supported");
			}
			catch (BinWriterException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new BinWriterException(path, type, ex);
			}
			finally
			{
				Trace(path);
			}
		}

		internal static void WriteTrailer(this BinaryWriter writer, BinaryPlistTrailer trailer)
		{
			try
			{
				writer.Write(new byte[] { 0, 0, 0, 0, 0 }); //unused[5]
				writer.Write(trailer.sortVersion);
				writer.Write(trailer.offsetIntSize);
				writer.Write(trailer.objectRefSize);
				writer.WriteBE(BitConverter.GetBytes(trailer.numObjects));
				writer.WriteBE(BitConverter.GetBytes(trailer.topObject));
				writer.WriteBE(BitConverter.GetBytes(trailer.offsetTableOffset));
			}
			catch (Exception ex)
			{
				throw new BinWriterException("trailer", typeof(BinaryPlistTrailer), ex);
			}
		}
	}

	public static class PropertyList2Binary
	{
		private static void FlattenPlist(object plist, ICollection<object> objlist, IDictionary<object, int> objtable, IDictionary<int, string> pathtable, string path)
		{
			int refnum;
			// Do not unique dictionaries or arrays, because: they
			// are slow to compare, and have poor hash codes.
			// Uniquing bools is unnecessary.
			if (plist is string || plist is long || plist is int || plist is DateTime ||
				plist is ulong || plist is BigInteger || plist is Guid)
			{
				if (objtable.TryGetValue(plist, out refnum)) // already in set
				{
					pathtable[refnum] += ";" + path;
					return;
				}
			}
			refnum = objlist.Count;
			objlist.Add(plist);
			objtable[plist] = refnum;
			pathtable[refnum] = path;
			if (plist is IDictionary<string, object>)
			{
				var dict = (IDictionary<string, object>)plist;
				var list = dict.GetKeysAndValues();
				var keys = dict.Select(item => item.Key).ToArray();
				int idx = 0;
				foreach (object obj in list)
					FlattenPlist(obj, objlist, objtable, pathtable, idx >= keys.Length ?
						String.Format("{0}/dict[{1}]", path, keys[idx++ - keys.Length]) : String.Format("{0}/dict.key[{1}]", path, idx++));
			}
			else if (plist is object[] || plist is ISet<object>)
			{
				var list = (IEnumerable<object>)plist;
				path += plist is object[] ? "/array" : "/set";
				int idx = 0;
				foreach (object obj in list)
					FlattenPlist(obj, objlist, objtable, pathtable, String.Format("{0}[{1}]", path, idx++));
			}
		}

		/// <summary>
		/// Write a property list to a stream, in binary format
		/// </summary>
		/// <param name="plist">plist is the property list to write (one of the basic property list types)</param>
		/// <param name="stream">stream is the destination of the property list</param>
		/// <returns></returns>
		public static long BinaryPlistWrite(object plist, Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException("stream");
			var objlist = new List<object>();
			var objtable = new Dictionary<object, int>(new DataComparer());
			var pathtable = new Dictionary<int, string>();
			FlattenPlist(plist, objlist, objtable, pathtable, "/plist[0.0]");

			int cnt = objlist.Count;
			var offsets = new long[cnt];

			var writer = new BinaryWriter(stream);
			writer.Write(Encoding.ASCII.GetBytes("bplist00")); // header

			BinaryPlistTrailer trailer = new BinaryPlistTrailer
			{
				sortVersion = 0,
				numObjects = cnt,
				topObject = 0, // true for this implementation
				objectRefSize = (byte)((long)cnt).BytesCount()
			};

			for (int idx = 0; idx < cnt; idx++)
			{
				offsets[idx] = stream.Position;
				object obj = objlist[idx];
				string path = "/plist[0.0]";
				int refnum;
				if (objtable.TryGetValue(obj, out refnum))
					path = pathtable[refnum];
				else if (idx > 0)
					path = String.Format("/plist[{0}]", idx);
				writer.WriteObject(obj, path, objtable, trailer.objectRefSize);
			}

			trailer.offsetTableOffset = stream.Position;
			trailer.offsetIntSize = (byte)trailer.offsetTableOffset.BytesCount();

			for (int idx = 0; idx < cnt; idx++)
			{
				long swapped = offsets[idx];
				writer.WriteBE(swapped.ToByteArray(trailer.offsetIntSize));
			}

			writer.WriteTrailer(trailer);
			return stream.Position;
		}

		/// <summary>
		/// Write a version 1.5 plist to a stream, in binary format;
		/// extra objects + inlined objects (no references, no uniquing)
		/// </summary>
		/// <param name="plist">plist is the property list to write (one of the basic property list types)</param>
		/// <param name="stream">stream is the destination of the property list</param>
		/// <returns></returns>
		public static long BinaryPlistWrite15(object plist, Stream stream)
		{
			var mem = new MemoryStream();
			var writer = new BinaryWriter(mem);

			writer.Write(Encoding.ASCII.GetBytes("bplist15")); // header
			writer.WriteProperty(0, 8); // header (byte length)
			writer.WriteProperty(0, 4); // header (crc)

			writer.WriteObject(plist, "/plist[1.5]");

			long len = mem.Position;
			mem.Seek(8, SeekOrigin.Begin);
			writer.WriteProperty(len, 8);

			mem.Seek(0, SeekOrigin.Begin);
			mem.WriteTo(stream);
			return mem.Position;
		}
	}
}
