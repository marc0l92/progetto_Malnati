﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace sync_clientWPF
{
	class SettingsManager
	{

		public SettingsManager()
		{
			if (!File.Exists("settings.xml"))
			{
				// Write default settings
				XmlWriterSettings settings = new XmlWriterSettings();
				settings.Indent = true;
				XmlWriter writer = XmlWriter.Create("settings.xml", settings);
				writer.WriteStartDocument();
				writer.WriteComment("This file is generated by the program.");
				writer.WriteStartElement("settings");
				writer.WriteStartElement("connection");
				writer.WriteAttributeString("address", "127.0.0.1");
				writer.WriteAttributeString("port", "55555");
				writer.WriteAttributeString("syncTime", "10");
				writer.WriteEndElement();
				writer.WriteStartElement("account");
				writer.WriteAttributeString("username", "");
				writer.WriteAttributeString("password", "");
				writer.WriteAttributeString("directory", "");
				writer.WriteEndElement();
				writer.WriteEndElement();
				writer.WriteEndDocument();
				writer.Flush();
				writer.Close();
			}
		}

		public void writeSetting(string section, string name, string value)
		{
			XmlDocument doc = new XmlDocument();
			doc.Load("settings.xml");
			XmlNode node = doc.SelectSingleNode("/settings/"+section);
			node.Attributes[name].Value = value;
			doc.Save("settings.xml");
		}

		public string readSetting(string section, string name)
		{
			String value = "";
			XmlReader reader = XmlReader.Create("settings.xml");

			while (reader.Read())
			{
				if (reader.NodeType == XmlNodeType.Element && reader.Name == section)
				{
					value = reader.GetAttribute(name);
					break;
				}
			}
			reader.Close();
			return value;
		}
	}
}