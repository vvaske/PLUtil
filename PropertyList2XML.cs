using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Numerics;
using System.Globalization;

namespace Apple.CoreFoundation
{
	internal class XmlWriterException: Exception
	{
		public XmlWriterException(string path, Type type, Exception ex) : base(ex.Message, ex)
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

	internal static class XMLWriterExtensions
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

		internal static void WriteDocumentElement(this XmlWriter writer, Action docType, string name, Action body)
		{
			writer.WriteStartDocument();
			docType();
			writer.WriteStartElement(name);
			body();
			writer.WriteEndElement();
			writer.WriteEndDocument();
		}

		internal static void WriteElement(this XmlWriter writer, string name, Action body)
		{
			writer.WriteStartElement(name);
			body();
			writer.WriteEndElement();
		}

		internal static void WriteProperty(this XmlWriter writer, string value)
		{
			writer.WriteElementString(StringTag, value);
		}

		internal static void WriteProperty(this XmlWriter writer, Guid uuid)
		{
			writer.WriteComment("UUID");
			writer.WriteElementString(StringTag, uuid.ToString("D"));
		}

		internal static void WriteProperty(this XmlWriter writer, object[] list, string path)
		{
			writer.WriteElement(ArrayTag, () =>
			{
				int idx = 0;
				foreach (object obj in list)
					writer.WriteObject(obj, String.Format("{0}/array[{1}]", path, idx++));
			});
		}

		internal static void WriteProperty(this XmlWriter writer, IDictionary<string, object> dict, string path)
		{
			writer.WriteElement(DictTag, () =>
			{
				var list = dict as IEnumerable<KeyValuePair<string, object>>;
				int idx = 0;
				foreach (var item in list)
				{
					string path1 = String.Format("{0}/dict.key[{1}]", path, idx++);
					string path2 = String.Format("{0}/dict[{1}]", path, item.Key);
					try
					{
						writer.WriteElementString(KeyTag, item.Key);
					}
					catch (Exception ex)
					{
						throw new XmlWriterException(path1, typeof(string), ex);
					}
					finally
					{
						Trace(path1);
					}
					writer.WriteObject(item.Value, path2);
				}
			});
		}

		internal static void WriteProperty(this XmlWriter writer, ISet<object> set, string path)
		{
			writer.WriteElement(ArrayTag, () =>
			{
				writer.WriteComment("Set");
				var list = set as IEnumerable<object>;
				int idx = 0;
				foreach (object obj in list)
					writer.WriteObject(obj, String.Format("{0}/array[{1}]", path, idx++));
			});
		}

		internal static void WriteProperty(this XmlWriter writer, byte[] data)
		{
			writer.WriteElement(DataTag, () => writer.WriteBase64(data, 0, data.Length));
		}

		internal static void WriteProperty(this XmlWriter writer, DateTime date)
		{
			writer.WriteElementString(DateTag, XmlConvert.ToString(date, "yyyy-MM-ddTHH:mm:ssZ"));
		}

		internal static void WriteProperty(this XmlWriter writer, float value)
		{
			writer.WriteComment("Float");
			string s;
			if (Single.IsNaN(value))
				s = "NaN";
			else if (Single.IsPositiveInfinity(value))
				s = "+Infinity";
			else if (Single.IsNegativeInfinity(value))
				s = "-Infinity";
			else
				s = XmlConvert.ToString(value);
			writer.WriteElementString(RealTag, s);
		}

		internal static void WriteProperty(this XmlWriter writer, double value)
		{
			string s;
			if (Double.IsNaN(value))
				s = "NaN";
			else if (Double.IsPositiveInfinity(value))
				s = "+Infinity";
			else if (Double.IsNegativeInfinity(value))
				s = "-Infinity";
			else
				s = XmlConvert.ToString(value);
			writer.WriteElementString(RealTag, s);
		}

		internal static void WriteProperty(this XmlWriter writer, long value)
		{
			writer.WriteElementString(IntegerTag, XmlConvert.ToString(value));
		}

		internal static void WriteProperty(this XmlWriter writer, ulong uid)
		{
			writer.WriteComment("UID");
			writer.WriteElementString(StringTag, String.Format(CultureInfo.InvariantCulture, "0x{0:X}", uid));
		}

		internal static void WriteProperty(this XmlWriter writer, BigInteger value)
		{
			writer.WriteElementString(IntegerTag, value.ToString(CultureInfo.InvariantCulture));
		}

		internal static void WriteProperty(this XmlWriter writer, bool value)
		{
			writer.WriteElementString(value ? TrueTag : FalseTag, null);
		}

		internal static void WriteObject(this XmlWriter writer, object obj, string path)
		{
			Type type = obj == null ? null : obj.GetType();
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
				//else if (obj == null)
				//	writer.WriteNullProperty();
				else if (obj is byte[])
					writer.WriteProperty((byte[])obj);
				else if (obj is DateTime)
					writer.WriteProperty((DateTime)obj);
				else if (obj is IDictionary<string, object>)
					writer.WriteProperty((IDictionary<string, object>)obj, path);
				else if (obj is object[])
					writer.WriteProperty((object[])obj, path);
				else if (obj is ISet<object>)
					writer.WriteProperty((ISet<object>)obj, path);
				else if (obj is Guid)
					writer.WriteProperty((Guid)obj);
				//else if (obj is URL)
				//	writer.WriteProperty((URL)obj);
				else if (obj is ulong)
					writer.WriteProperty((ulong)obj);
				else
					throw new NotSupportedException("object type not supported");
			}
			catch (XmlWriterException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new XmlWriterException(path, type, ex);
			}
			finally
			{
				Trace(path);
			}
		}
	}

	public static class PropertyList2XML
	{
		public const string PListTag = "plist";

		public static void GenerateXMLPropertyListToData(XmlWriter writer, object plist)
		{
			writer.WriteDocumentElement(() =>
				writer.WriteDocType(PListTag, "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
				PListTag, () =>
				{
					writer.WriteAttributeString("version", "1.0");
					writer.WriteObject(plist, "/plist[1.0]");
				});
		}
	}
}
