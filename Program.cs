using System;
using System.IO;
using System.Xml;
using System.Text;
using System.Xml.XPath;
using System.Linq;
using System.Collections.Generic;

namespace Apple.CoreFoundation
{
	class Program
	{
		const string txtUsage = @"{0}: [command_option] [other_options] file...
Command options are (-lint is the default):
 -help          show this message and exit
 -lint          check the property list files for syntax errors
 -convert fmt   rewrite property list files in format
                fmt is one of: xml1 binary1 binary15
There are some additional optional arguments:
 -s             be silent on success
 -o path        specify alternate file path name for result;
                the -o option is used with -convert, and is only
                useful with single file argument;
                the path '-' means stdout
 -e extension   specify alternate extension for converted files";

		//const string txtSilentError = "-s doesn't make a lot of sense with -p.";
		const string txtOutputError = "-o is only used with -convert.";
		const string txtExtError = "-e is only used with -convert.";
		const string txtUnknown = "unrecognized option: {0}";
		const string txtNoFileError = "No files specified.";
		const string txtManyFilesError = "Too many files specified.";
		const string txtStdInError = "Unable to read file from standard input";
		const string txtFileError = "{0}: file does not exist or is not readable or is not a regular file";
		const string txtCheckOk = "{0}: OK";
		const string txtBinFormat = "binary1";
		const string txtBin15Format = "binary15";
		const string txtXMLFormat = "xml1";
		//const string txtJSONFormat = "json";
		const string txtUnknownFormat = "Unknown format specifier: {0}";
		const string txtOutputEmpty = "Missing argument for -o.";
		const string txtExtEmpty = "Missing argument for -e.";
		const string txtPListError = ": Property List error: ";//" / JSON error: {2}";
		const string txtUnknownType = "{0}: invalid object in plist for destination format";
		const string txtConvertError = "Missing format specifier for command.";

		[System.Runtime.InteropServices.DllImport("msvcrt.dll")]
		static extern IntPtr memcmp(byte[] a, byte[] b, IntPtr count);

		static int? WithdrawOpt(IList<string> args, string opt)
		{
			string opt1 = opt.Substring(0, 1);
			string arg = args.FirstOrDefault(s =>
				String.Compare(s, "-" + opt, StringComparison.OrdinalIgnoreCase) == 0 ||
				String.Compare(s, "-" + opt1, StringComparison.OrdinalIgnoreCase) == 0 ||
				String.Compare(s, "/" + opt, StringComparison.OrdinalIgnoreCase) == 0 ||
				String.Compare(s, "/" + opt1, StringComparison.OrdinalIgnoreCase) == 0);
			if (arg == null) return null;
			int pos = args.IndexOf(arg);
			args.RemoveAt(pos);
			return pos;
		}

		static string WithdrawNextOpt(IList<string> args, int? opt)
		{
			if (opt == null || opt.Value >= args.Count)
				return null;
			string arg = args[opt.Value];
			args.RemoveAt(opt.Value);
			return arg;
		}

		static string ParseOpt(IList<string> args, out string fn, out bool isLint, out string fmt, out bool isSilent, out string path, out string ext)
		{
			//-convert fmt
			var optConv = WithdrawOpt(args, "convert");
			fmt = WithdrawNextOpt(args, optConv);
			//-lint
			var optLint = WithdrawOpt(args, "lint");
			isLint = optLint != null || optConv == null;
			//-s
			var optSilent = WithdrawOpt(args, "s");
			isSilent = optSilent != null;
			//-o path
			var optOut = WithdrawOpt(args, "o");
			path = WithdrawNextOpt(args, optOut);
			//-e extension
			var optExt = WithdrawOpt(args, "e");
			ext = WithdrawNextOpt(args, optExt);
			fn = args.FirstOrDefault();
			//-help
			if (args.Count == 0 || WithdrawOpt(args, "help") != null)
				return String.Format(txtUsage, Environment.GetCommandLineArgs()[0]);
			//validation
			if (optConv != null && fmt == null)
				return txtConvertError;
			if (optOut != null && path == null)
				return txtOutputEmpty;
			if (optExt != null && ext == null)
				return txtExtEmpty;
			string optUnk = args.FirstOrDefault(s => s.StartsWith("-") || s.StartsWith("/"));
			if (optUnk != null)
				return String.Format(txtUnknown, optUnk);
			if (args.Count == 0)
				return txtNoFileError;
			if (args.Count > 1 || optOut != null && fn.Contains("*"))
				return txtManyFilesError;
			if (optConv != null)
			{
				fmt = fmt.ToLower();
				if (fmt != txtBinFormat && fmt != txtBin15Format && fmt != txtXMLFormat)
					return String.Format(txtUnknownFormat, fmt);
			}
			//combinations
			if (optOut != null && (optConv == null || isLint || optExt != null))
				return txtOutputError;
			if (optExt != null && (optConv == null || isLint || optOut != null))
				return txtExtError;
			return null;
		}

		static void Main(string[] args)
		{
			bool isLint, isSilent;
			string fmt, path, ext, fn;
			string msg = ParseOpt(new List<string>(args), out fn, out isLint, out fmt, out isSilent, out path, out ext);
			if (msg != null)
			{
				Console.WriteLine(msg);
				return;
			}
			var fns = new List<string>();
			if (fn.Contains("*"))
			{
				string fnp = Path.GetDirectoryName(fn.Replace('*', '_'));
				fnp = fnp == String.Empty ? Directory.GetCurrentDirectory() : Path.GetFullPath(fnp);
				int suf = Path.GetFileName(fn.Replace('*', '_')).Length;
				foreach (string file in Directory.EnumerateFiles(fnp, fn.Substring(fn.Length - suf)))
					fns.Add(file);
			}
			else fns.Add(fn);

			foreach (string file in fns)
				if (isLint)
				{
					ReadPropertyList(file, isSilent ? null : "{0}: Valid {1} property list {2}");
				}
				else
				{
					object plist = ReadPropertyList(file, isSilent ? null : "{0} ({1}{2}) =>");
					if (plist == null) continue;
					string ofn = path ?? (ext == null ? file : Path.ChangeExtension(file, ext));
					if (fmt == txtXMLFormat)
						WriteXMLPropertyList(plist, ofn, isSilent ? null : "{0} (XML)");
					else
						WriteBinPropertyList(plist, ofn, fmt == txtBin15Format, isSilent ? null : "{0} (Binary{1})");
				}
		}

		static MemoryStream CreateBuffer(Stream stream)
		{
			int readAmount, readTotal = 0;
			if (stream.CanSeek)
			{
				var buf = new byte[(int)stream.Length];
				while ((readAmount = stream.Read(buf, readTotal, buf.Length - readTotal)) > 0 &&
					(readTotal = readTotal + readAmount) < buf.Length) ;
				return new MemoryStream(buf);
			}
			else
			{
				var buf = new byte[8192];
				var mem = new MemoryStream();
				while ((readAmount = stream.Read(buf, 0, buf.Length)) > 0)
					mem.Write(buf, 0, readAmount);
				mem.Seek(0L, SeekOrigin.Begin);
				return mem;
			}
		}

		static object ReadPropertyList(string fn, string result)
		{
			object plist;
			string version = null;
			MemoryStream mem = null;
			string type = null;
			try
			{
				using (var ifs = new FileStream(fn, FileMode.Open))
					mem = CreateBuffer(ifs);
				var reader = new BinaryReader(mem, Encoding.UTF8);
				string header = new String(reader.ReadChars(6));
				mem.Seek(0L, SeekOrigin.Begin);
				if (header == "bplist")
				{
					type = "Binary";
					plist = Binary2PropertyList.BinaryPlistCreate(mem, out version);
#if DEBUG
					if (result != null && !result.Contains("=>"))
					{
						var mem2 = new MemoryStream();
						if (version == "1.5")
							PropertyList2Binary.BinaryPlistWrite15(plist, mem2);
						else
							PropertyList2Binary.BinaryPlistWrite(plist, mem2);
						if (!DataComparer.Equals(mem.ToArray(), mem2.ToArray()))
						{
							using (var tmp = new FileStream(fn + ".tmp", FileMode.Create))
								mem2.WriteTo(tmp);
							throw new InvalidOperationException("Read/write operation failed: " + fn + ".tmp");
						}
					}
#endif
					version = " v" + version;
				}
				else if (header.StartsWith("<?xml"))
				{
					type = "XML";
					var doc = new XPathDocument(mem);
					var nav = doc.CreateNavigator();
					plist = XML2PropertyList.PropertyListCreateFromXML(nav);
#if DEBUG
					if (result != null && !result.Contains("=>"))
					{
						var mem2 = new MemoryStream();
						using (var writer = XmlWriter.Create(mem2, new XmlWriterSettings()
						{
							Encoding = Encoding.UTF8,
							Indent = true,
							IndentChars = "\t",
							OmitXmlDeclaration = false,
							CloseOutput = false
						}))
							PropertyList2XML.GenerateXMLPropertyListToData(writer, plist);
						if (!DataComparer.Equals(mem.ToArray(), mem2.ToArray()))
						{
							using (var tmp = new FileStream(fn + ".tmp", FileMode.Create))
								mem2.WriteTo(tmp);
							throw new InvalidOperationException("Read/write operation failed: " + fn + ".tmp");
						}
					}
#endif
				}
				else
					throw new NotSupportedException("Unknown property list file format");
				if (result != null)
					Console.WriteLine(result, fn, type, version);
				return plist;
			}
			catch (XmlException ex)
			{
				Console.WriteLine("{0}({1},{2})" + txtPListError + "{3}", fn, ex.LineNumber, ex.LinePosition, (ex.InnerException ?? ex).Message);
				return null;
			}
			catch (Exception ex)
			{
				if (mem != null && mem.CanRead)
					Console.WriteLine("{0}+{1:X}" + txtPListError + "{2}", fn, mem.Position, ex.Message);
				else
					Console.WriteLine("{0}" + txtPListError + "{1}", fn, ex.Message);
				return null;
			}
		}

		static void WriteBinPropertyList(object plist, string fn, bool version15, string result)
		{
			var mem = new MemoryStream();
			try
			{
				using (var ofs = new FileStream(fn, FileMode.Create))
				{
					if (version15)
						PropertyList2Binary.BinaryPlistWrite15(plist, mem);
					else
						PropertyList2Binary.BinaryPlistWrite(plist, mem);
					mem.Seek(0L, SeekOrigin.Begin);
					mem.WriteTo(ofs);
					if (result != null)
						Console.WriteLine(result, fn, version15 ? " v1.5" : "");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("{0}" + txtPListError + "{1}", fn, ex.Message);
			}
		}

		static void WriteXMLPropertyList(object plist, string fn, string result)
		{
			var mem = new MemoryStream();
			try
			{
				using (var ofs = new FileStream(fn, FileMode.Create))
				{
					using (var writer = XmlWriter.Create(mem, new XmlWriterSettings()
					{
						Encoding = Encoding.UTF8,
						Indent = true,
						IndentChars = "\t",
						OmitXmlDeclaration = false,
						CloseOutput = false
					}))
						PropertyList2XML.GenerateXMLPropertyListToData(writer, plist);
					mem.Seek(0L, SeekOrigin.Begin);
					mem.WriteTo(ofs);
					if (result != null)
						Console.WriteLine(result, fn);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("{0}" + txtPListError + "{1}", fn, ex.Message);
			}
		}
	}
}
