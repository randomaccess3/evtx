using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using evtx.Tags;
using Serilog;
using ServiceStack;

namespace evtx;

public class EventRecord
{
    private static readonly Regex UnescapedAmpersandRegex =
        new("&(?!amp;|lt;|gt;|quot;|apos;|#\\d+;|#x[0-9A-Fa-f]+;)", RegexOptions.Compiled);

    public EventRecord(BinaryReader recordData, int recordPosition, ChunkInfo chunk)
    {
        RecordPosition = recordPosition;

        ChunkNumber = chunk.ChunkNumber;

        recordData.ReadInt32(); //signature

        Size = recordData.ReadUInt32();
        RecordNumber = recordData.ReadInt64();
        Timestamp = DateTimeOffset.FromFileTime(recordData.ReadInt64()).ToUniversalTime();

        if (recordData.PeekChar() != 0xf)
        {
            throw new Exception("Payload does not start with 0x1f!");
        }

        Log.Verbose(
            "Record position: 0x{RecordPosition:X4} Record #: {RecordNumber} Timestamp: {Timestamp:yyyy-MM-dd HH:mm:ss.fffffff}",RecordPosition,RecordNumber.ToString().PadRight(3),Timestamp);

        Nodes = new List<IBinXml>();

        var eof = false;

        while (eof == false)
        {
            var nextTag = TagBuilder.BuildTag(recordPosition, recordData, chunk);
            Nodes.Add(nextTag);

            if (nextTag is EndOfBXmlStream)
                  
            {
                //nothing left to do, so exit
                eof = true;

                //check here if there is a 0x2a0x2a and if so, another record!

                var found2a = true; //danderspritz test
                var maxCount = 0;
                while (maxCount < 15 && recordData.BaseStream.Position < recordData.BaseStream.Length)
                {
                    if (recordData.ReadByte() == 0x2a)
                    {
                        break;
                    }
                    
                    maxCount += 1;
                }

                if (recordData.BaseStream.Position >= recordData.BaseStream.Length)
                {
                    found2a = false;
                }
                    
                if (found2a)
                {
                    //a secondary check to eliminate false positives
                    if (recordData.ReadByte() == 0x2a)
                    {
                        //back up two
                        recordData.BaseStream.Seek(-2, SeekOrigin.Current);
                    
                        ExtraDataOffset = recordData.BaseStream.Position;
                    }
                        
                      
                }
                    
            }
        }

        BuildProperties();
    }

    public string PayloadData1 { get; private set; }
    public string PayloadData2 { get; private set; }
    public string PayloadData3 { get; private set; }
    public string PayloadData4 { get; private set; }
    public string PayloadData5 { get; private set; }
    public string PayloadData6 { get; private set; }
    public string UserName { get; private set; }
    public string RemoteHost { get; private set; }
    public string ExecutableInfo { get; private set; }
    public string MapDescription { get; private set; }

    public int ChunkNumber { get; }

    public string Computer { get; private set; }

    public string Payload { get; set; }

    public string UserId { get; private set; }
    public string Channel { get; private set; }
    public string Provider { get; private set; }
    public int EventId { get; private set; }
    public string EventRecordId { get; private set; }
    public int ProcessId { get; private set; }
    public int ThreadId { get; private set; }
    public string Level { get; private set; }
    public string Keywords { get; private set; }
    public string SourceFile { get; set; }

    /// <summary>
    ///     Some providers (e.g. manifest-based/self-describing providers like Microsoft-Windows-Perflib) embed a
    ///     &lt;RenderingInfo&gt; element alongside &lt;System&gt;. It re-uses several element names found in
    ///     &lt;System&gt; (Level, Channel, Provider, Keywords, etc.) but with different shapes/content, so it is
    ///     parsed separately to avoid clobbering the authoritative &lt;System&gt; values. All of its fields are
    ///     combined into a single JSON blob here, mirroring how <see cref="Payload" /> holds EventData/UserData as a
    ///     single serialized value.
    /// </summary>
    public string RenderingInfo { get; private set; }

    public long ExtraDataOffset { get; set; }
    public bool HiddenRecord { get; set; }

    /// <summary>
    ///     This should match the Timestamp pulled from the data, but this one is explicitly from the XML via the substitution
    ///     values
    /// </summary>

    public DateTimeOffset TimeCreated { get; private set; }

    [IgnoreDataMember] public List<IBinXml> Nodes { get; set; }

    [IgnoreDataMember] public int RecordPosition { get; }

    [IgnoreDataMember] public uint Size { get; }

    public long RecordNumber { get; }

    [IgnoreDataMember] public DateTimeOffset Timestamp { get; }

    public void BuildProperties()
    {
        var xml = ConvertPayloadToXml();

        var reader = XmlReader.Create(new StringReader(xml));
        reader.MoveToContent();

        // Some providers (e.g. manifest-based/self-describing providers like Microsoft-Windows-Perflib) embed a
        // <RenderingInfo> element alongside <System>. It re-uses several element names from <System> (Level,
        // Channel, Provider, Keywords, etc.) but with different shapes/content (e.g. Keywords is a list of
        // <Keyword> child elements instead of a single hex string). It is parsed into its own DTO below so it
        // never overwrites/corrupts the authoritative <System> values, then combined into a single RenderingInfo
        // JSON value so its otherwise-unique data (rendered Message, friendly Task name, etc.) isn't silently
        // discarded.
        var insideRenderingInfo = false;
        RenderingInfoData renderingInfoData = null;

        // Parse the file and display each of the nodes.
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "RenderingInfo")
            {
                insideRenderingInfo = false;
                continue;
            }

            if (reader.IsStartElement())
            {
                if (reader.Name == "RenderingInfo")
                {
                    insideRenderingInfo = true;
                    renderingInfoData ??= new RenderingInfoData();
                    continue;
                }

                if (insideRenderingInfo)
                {
                    try
                    {
                        switch (reader.Name)
                        {
                            case "Level":
                                renderingInfoData.Level = reader.ReadElementContentAsString();
                                break;
                            case "Opcode":
                                renderingInfoData.Opcode = reader.ReadElementContentAsString();
                                break;
                            case "Task":
                                renderingInfoData.Task = reader.ReadElementContentAsString();
                                break;
                            case "Channel":
                                renderingInfoData.Channel = reader.ReadElementContentAsString();
                                break;
                            case "Provider":
                                renderingInfoData.Provider = reader.ReadElementContentAsString();
                                break;
                            case "Keywords":
                                renderingInfoData.Keywords = ReadRenderingInfoKeywords(reader);
                                break;
                            case "Message":
                                renderingInfoData.Message = reader.ReadElementContentAsString();
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(
                            "Record # {RecordNumber}: Unable to parse RenderingInfo XML element {ElementName}. Error: {Message}",
                            RecordNumber, reader.Name, ex.Message);
                    }

                    continue;
                }

                try
                {
                    switch (reader.Name)
                    {
                        case "Computer":
                            reader.Read();
                            Computer = reader.Value;
                            break;
                        case "Channel":
                            reader.Read();
                            Channel = reader.Value;
                            break;
                        case "EventRecordID":
                            EventRecordId = reader.ReadElementContentAsString();
                            break;
                        case "EventID":
                            EventId = reader.ReadElementContentAsInt();
                            break;
                        case "Level":
                            var lvl = reader.ReadElementContentAsInt();

                            switch (lvl)
                            {
                                case 0:
                                    Level = "LogAlways";
                                    break;
                                case 1:
                                    Level = "Critical";
                                    break;
                                case 2:
                                    Level = "Error";
                                    break;
                                case 3:
                                    Level = "Warning";
                                    break;
                                case 4:
                                    Level = "Info";
                                    break;
                                case 5:
                                    Level = "Verbose";
                                    break;

                                case 8:
                                    Level = "Success";
                                    break;
                                case 16:
                                    Level = "Failure";
                                    break;
                                default:
                                    Level = lvl.ToString();
                                    break;
                            }

                            break;

                        case "Keywords":

                            var kw = reader.ReadElementContentAsString();

                            switch (kw)
                            {
                                case "0x8010000000000000":
                                    Keywords = "Audit failure";
                                    break;
                                case "0x8020000000000000":
                                    Keywords = "Audit success";
                                    break;
                                case "0x8000000000000010":
                                    Keywords = "Time";
                                    break;
                                case "0x8000000000000080":
                                    Keywords = "State";
                                    break;
                                case "0x8000000000000040":
                                    Keywords = "Reboot";
                                    break;
                                case "0x8000000000000018":
                                    Keywords = "Installation";
                                    break;
                                case "0x8000000000000014":
                                    Keywords = "Download";
                                    break;
                                case "0x8080000000000000":
                                    Keywords = "Audit success, classic";
                                    break;
                                case "0x8000000000000000":
                                    Keywords = "Classic";
                                    break;
                                default:
                                    Keywords = kw;
                                    break;
                            }

                            break;

                        case "TimeCreated":
                            var st = reader.GetAttribute("SystemTime");
                            TimeCreated = DateTimeOffset.Parse(st, null, DateTimeStyles.AssumeUniversal).ToUniversalTime();
                            break;
                        case "Provider":
                            Provider = reader.GetAttribute("Name");
                            break;
                        case "Execution":
                            var pid = reader.GetAttribute("ProcessID");
                            var tid = reader.GetAttribute("ThreadID");
                            if (pid != null)
                            {
                                ProcessId = int.Parse(pid);
                            }

                            if (tid != null)
                            {
                                ThreadId = int.Parse(tid);
                            }

                            break;
                        case "Security":
                            UserId = reader.GetAttribute("UserID");
                            break;

                        case "EventData":
                        case "UserData":
                            Payload = reader.ReadOuterXml();

                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("Record # {RecordNumber}: Unable to parse XML element {ElementName}. Error: {Message}",RecordNumber,reader.Name,ex.Message);
                }
            }
        }

        if (renderingInfoData != null)
        {
            RenderingInfo = renderingInfoData.ToJson();
        }

        if (Payload == null)
        {
            try
            {
                reader = XmlReader.Create(new StringReader(xml));
                reader.MoveToContent();

                if (reader.ReadToDescendant("System"))
                {
                    reader.ReadOuterXml();
                    reader.ReadOuterXml();
                    Payload = reader.ReadOuterXml();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Record # {RecordNumber}: Unable to extract payload data. Error: {Message}",RecordNumber,ex.Message);
            }

            Payload ??= string.Empty;

        }

        if (EventLog.EventLogMaps.Count == 0)
        {
            return;
        }

        if (Channel.IsNullOrEmpty() || Provider.IsNullOrEmpty())
        {
            return;
        }

        var mapKey = $"{EventId}-{Channel.ToUpperInvariant()}-{Provider.ToUpperInvariant()}";

        if (!EventLog.EventLogMaps.ContainsKey(mapKey))
        {
            return;
        }

        var docNav = new XPathDocument(new StringReader(xml));
        var nav = docNav.CreateNavigator();

        Log.Verbose("Found map for Event ID {EventId} with Channel {Channel} and Provider {Provider}!",EventId,Channel,Provider);
        var map = EventLog.EventLogMaps[mapKey];

        MapDescription = map.Description;

        Log.Debug("Processing map with description {Description}, event id: {EventId}",map.Description,map.EventId);

        foreach (var mapEntry in map.Maps)
        {
            var valProps = new Dictionary<string, string>();

            foreach (var me in mapEntry.Values)
            {
                //xpath out variables
                var propVal = nav.SelectSingleNode(me.Value); 
                if (propVal != null)
                {
                    var propValue = propVal.Value;

                    if (me.Refine.IsNullOrEmpty() == false)
                    {
                        var hits = new List<string>();

                        //regex time
                        try
                        {
                            var regexObj = new Regex(me.Refine, RegexOptions.IgnoreCase);
                            var allMatchResults = regexObj.Matches(propValue);
                            if (allMatchResults.Count > 0)
                            {
                                // Access individual matches using allMatchResults.Item[]
                                foreach (Match allMatchResult in allMatchResults)
                                {
                                    hits.Add(allMatchResult.Value);
                                }

                                propValue = string.Join(" | ", hits);
                            }
                        }
                        catch (ArgumentException)
                        {
                            // Syntax error in the regular expression
                        }
                    }

                    var lu = map.Lookups?.SingleOrDefault(t =>
                        t.Name.ToUpperInvariant() == me.Name.ToUpperInvariant());

                    if (lu != null)
                    {
                           
                        if (lu.Values.ContainsKey(propValue))
                        {
                            propValue = lu.Values[propValue]; //set it to lookup value
                        }
                        else
                        {
                            propValue = $"{lu.Default} ({propValue})"; //include the default and original value
                        }
                    }

                    valProps.Add(me.Name, propValue);
                }
                else
                {
                    valProps.Add(me.Name, string.Empty);
                    Log.Warning("Record # {RecordNumber} (Event Record Id: {EventRecordId}): In map for event {EventId}, Property {Value} not found! Replacing with empty string",RecordNumber,EventRecordId,map.EventId,me.Value);
                }
            }

            //we have the values, now substitute
            var propertyValue = mapEntry.PropertyValue;
            foreach (var valProp in valProps)
            {
                propertyValue = propertyValue.Replace($"%{valProp.Key}%", valProp.Value);
            }

            var propertyToUpdate = mapEntry.Property.ToUpperInvariant();

            if (valProps.Count == 0)
            {
                propertyToUpdate = "NOMATCH"; //prevents variables from showing up in the CSV
            }

            //we should now have our new value, so stick it in its place
            switch (propertyToUpdate)
            {
                case "USERNAME":
                    UserName = propertyValue;
                    break;
                case "REMOTEHOST":
                    RemoteHost = propertyValue;
                    break;
                case "EXECUTABLEINFO":
                    ExecutableInfo = propertyValue;
                    break;
                case "PAYLOADDATA1":
                    PayloadData1 = propertyValue;
                    break;
                case "PAYLOADDATA2":
                    PayloadData2 = propertyValue;
                    break;
                case "PAYLOADDATA3":
                    PayloadData3 = propertyValue;
                    break;
                case "PAYLOADDATA4":
                    PayloadData4 = propertyValue;
                    break;
                case "PAYLOADDATA5":
                    PayloadData5 = propertyValue;
                    break;
                case "PAYLOADDATA6":
                    PayloadData6 = propertyValue;
                    break;
                case "NOMATCH":
                    //when a property was not found.
                    break;
                default:
                    Log.Warning("Unknown property name {PropertyToUpdate}! Dropping mapping value of {PropertyValue}",propertyToUpdate,propertyValue);
                    break;
            }
        }
    }

    /// <summary>
    ///     RenderingInfo's Keywords element can be either plain text (a single hex bitmask, matching System's shape)
    ///     or a list of child &lt;Keyword&gt; elements (one per named bit). This reads either shape and returns a
    ///     comma-separated string of the resolved values.
    /// </summary>
    private static string ReadRenderingInfoKeywords(XmlReader reader)
    {
if (reader.IsEmptyElement)
{
    return string.Empty;
}

        var keywords = new List<string>();

        using (var subtree = reader.ReadSubtree())
        {
            subtree.Read(); // move onto the Keywords start element

            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "Keyword")
                {
                    keywords.Add(subtree.ReadElementContentAsString());
                }
                else if (subtree.NodeType == XmlNodeType.Text)
                {
                    keywords.Add(subtree.Value);
                }
            }
        }

        // ReadSubtree leaves the original reader positioned on Keywords' end element, so advance past it
        reader.Read();

        return string.Join(",", keywords);
    }

    public string ConvertPayloadToXml()
    {
        var ti = Nodes.SingleOrDefault(t => t.TagType == TagBuilder.BinaryTag.TemplateInstance);

        if (ti == null)
        {
            return "Record does not contain a template instance!";
        }

        ti = (TemplateInstance) ti;

        var xmld = new XmlDocument();
        var rawXml = ti.AsXml(null, RecordPosition);

        try
        {
            xmld.LoadXml(rawXml);
        }
        catch (XmlException)
        {
            rawXml = UnescapedAmpersandRegex.Replace(rawXml, "&amp;");
            xmld.LoadXml(rawXml);
        }

        return Regex.Replace(xmld.Beautify(), " xmlns.+\">", ">",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    public override string ToString()
    {
        return
            $"Record position: 0x{RecordPosition:X4} Record #: {RecordNumber.ToString().PadRight(3)} Timestamp: {Timestamp:yyyy-MM-dd HH:mm:ss.fffffff} Event ID: {EventId}";
    }
}

/// <summary>
///     Holds the fields parsed from a record's optional &lt;RenderingInfo&gt; element. Serialized as a single JSON
///     blob into <see cref="EventRecord.RenderingInfo" />, mirroring how <see cref="EventRecord.Payload" /> holds
///     EventData/UserData as a single serialized value.
/// </summary>
internal class RenderingInfoData
{
    public string Level { get; set; }
    public string Opcode { get; set; }
    public string Task { get; set; }
    public string Channel { get; set; }
    public string Provider { get; set; }
    public string Keywords { get; set; }
    public string Message { get; set; }
}