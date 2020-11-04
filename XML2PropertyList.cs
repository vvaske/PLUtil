using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.XPath;
using System.IO;
using System.Xml;
using System.Globalization;
using System.Numerics;

namespace Apple.CoreFoundation
{
	internal class XMLReaderException: Exception, IXmlLineInfo
	{
		public XMLReaderException(string path, string tag, IXmlLineInfo info, Exception ex)
			: base(ex.Message, ex)
		{
			Path = path;
			Tag = tag;
			if (info != null && info.HasLineInfo())
			{
				LineNumber = info.LineNumber;
				LinePosition = info.LinePosition;
			}
		}

		public string Path { get; private set; }
		public string Tag { get; private set; }
		public int? LineNumber { get; private set; }
		public int? LinePosition { get; private set; }

		public override string Message
		{
			get
			{
				return String.Format("{0}\nat {1}/{2}:", base.Message, Path, Tag);
			}
		}

		bool IXmlLineInfo.HasLineInfo()
		{
			return LineNumber != null && LinePosition != null;
		}

		int IXmlLineInfo.LineNumber
		{
			get { return (int)LineNumber; }
		}

		int IXmlLineInfo.LinePosition
		{
			get { return (int)LinePosition; }
		}
	}

	internal static class XMLReaderExtensions
	{
		public const string ArrayTag = "array";
		public const string DictTag = "dict";
		public const string KeyTag = "key";
		public const string StringTag = "string";
		public const string DataTag = "data";
		public const string DateTag = "date";
		public const string RealTag = "real";
		public const string IntegerTag = "integer";
		public const string TrueTag = "true";
		public const string FalseTag = "false";

		[System.Diagnostics.Conditional("DEBUG")]
		private static void Trace(string path)
		{
			Console.WriteLine(path);
		}

		internal static object ReadStringProperty(this XPathNavigator reader, bool isUUID = false, bool isUID = false)
		{
			string s = reader.Value;
			if (isUUID)
				return Guid.Parse(s);
			if (isUID)
				return XmlConvert.ToUInt64(s);
			return s;
		}

		internal static object ReadRealProperty(this XPathNavigator reader, bool isFloat = false)
		{
			string s = reader.Value;
			if (String.Compare(s, "NaN", StringComparison.OrdinalIgnoreCase) == 0)
				return isFloat ? (object)Single.NaN : (object)Double.NaN;
			else if (String.Compare(s, "+Infinity") == 0 || String.Compare(s, "+INF") == 0 ||
				String.Compare(s, "Infinity") == 0 || String.Compare(s, "INF") == 0)
				return isFloat ? (object)Single.PositiveInfinity : (object)Double.PositiveInfinity;
			else if (String.Compare(s, "-Infinity") == 0 || String.Compare(s, "-INF") == 0)
				return isFloat ? (object)Single.NegativeInfinity : (object)Double.NegativeInfinity;
			else
				return isFloat ? (object)XmlConvert.ToSingle(s) : (object)XmlConvert.ToDouble(s);
		}

		internal static object ReadIntProperty(this XPathNavigator reader)
		{
			string s = reader.Value;
			if (s == null) s = String.Empty;
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				if (s.Substring(2).TrimStart('0').Length > 32) //32 hex symbols
					throw new OverflowException("Too big integer");
			}
			else if (s.StartsWith("-") || s.StartsWith("+"))
			{
				if (s.Substring(1).TrimStart('0').Length > 39) //-3.4e+38 .. +3.4e+38
					throw new OverflowException("Too big integer");
			}
			else
			{
				if (s.TrimStart('0').Length > 39) //3.4e+38
					throw new OverflowException("Too big integer");
			}
			var bignum = BigInteger.Parse(s, NumberStyles.Integer, NumberFormatInfo.InvariantInfo);
			if (bignum.ToByteArray().Length > 16)
				throw new OverflowException("Too big integer");
			if (bignum > long.MaxValue || bignum < long.MinValue)
				return bignum;
			return (long)bignum;
		}

		internal static byte[] ReadDataProperty(this XPathNavigator reader)
		{
			string s = reader.Value;
			return Convert.FromBase64String(s);
		}

		internal static DateTime ReadDateProperty(this XPathNavigator reader)
		{
			string s = reader.Value;
			return XmlConvert.ToDateTime(s, "yyyy-MM-ddTHH:mm:ssZ");
		}

		internal static IDictionary<string, object> ReadDictProperty(this XPathNavigator reader, string path)
		{
			var childs = reader.SelectChildren(XPathNodeType.Element);
			var keys = new List<object>();
			var values = new List<object>();
			foreach (XPathNavigator child in childs)
			{
				if (child.Name == KeyTag)
				{
					string childpath = String.Format("{0}/dict.key[{1}]", path, keys.Count);
					try
					{
						if (keys.Count != values.Count)
							throw new InvalidDataException("Unexpected key");
						string key = child.Value;
						keys.Add(key);
					}
					catch (Exception ex)
					{
						throw new XMLReaderException(childpath, child.Name, child as IXmlLineInfo, ex);
					}
					finally
					{
						Trace(childpath + "#");
					}
				}
				else
				{
					string childpath = String.Format("{0}/dict[{1}]", path, keys.Last());
					if (keys.Count != values.Count + 1)
						throw new XMLReaderException(childpath, child.Name, child as IXmlLineInfo, new InvalidDataException("Unexpected value"));
					object obj = child.ReadObject(childpath);
					values.Add(obj);
				}
			}
			keys.AddRange(values);
			return new Dict(keys.ToArray());
		}

		internal static object ReadArrayProperty(this XPathNavigator reader, string path)
		{
			var comment = reader.CreateNavigator();
			comment.MoveToFirstChild();
			bool isSet = comment.NodeType == XPathNodeType.Comment && comment.Value == "Set";
			var childs = reader.SelectChildren(XPathNodeType.Element);
			var values = new List<object>();
			path += isSet ? "/set" : "/array";
			foreach (XPathNavigator child in childs)
			{
				string childpath = String.Format("{0}[{1}]", path, values.Count);
				object obj = child.ReadObject(childpath);
				values.Add(obj);
			}
			return isSet ? (object)new Set(values.ToArray()) : (object)values.ToArray();
		}

		internal static object ReadObject(this XPathNavigator reader, string path)
		{
			try
			{
				string comment = null;
				if (reader.NodeType == XPathNodeType.Comment)
				{
					comment = reader.Value;
					reader.MoveToNext(XPathNodeType.Element);
				}
				switch (reader.Name)
				{
				case StringTag:
					return reader.ReadStringProperty(isUUID : comment == "UUID", isUID : comment == "UID");
				case RealTag:
					return reader.ReadRealProperty(isFloat : comment == "Float");
				case IntegerTag:
					return reader.ReadIntProperty();
				case TrueTag:
					return true;
				case FalseTag:
					return false;
				case DataTag:
					return reader.ReadDataProperty();
				case DateTag:
					return reader.ReadDateProperty();
				case DictTag:
					return reader.ReadDictProperty(path);
				case ArrayTag:
					return reader.ReadArrayProperty(path);
				default:
					throw new InvalidDataException("Unexpected tag: " + reader.Name);
				}
			}
			catch (XMLReaderException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new XMLReaderException(path, reader.Name, reader as IXmlLineInfo, ex);
			}
			finally
			{
				Trace(path + "#");
			}
		}
	}

	public static class XML2PropertyList
	{
		public const string PListTag = "plist";

		public static object PropertyListCreateFromXML(XPathNavigator reader)
		{
			if (!reader.MoveToFirstChild() || reader.Name != PListTag)
				throw new InvalidDataException("Unknown root element");
			reader.MoveToFirstChild();
			return reader.ReadObject(String.Format("/plist[{0}]", reader.GetAttribute("version", String.Empty)));
		}
	}
}
