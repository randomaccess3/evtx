using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Serilog;

namespace evtx.Tags;

public class Value : IBinXml
{
    public Value(long recordPosition, BinaryReader dataStream, ChunkInfo chunk)
    {

        RecordPosition = recordPosition;

        ValueDataType = (TagBuilder.ValueType) dataStream.ReadByte();

        Size = dataStream.ReadInt16();

        ValueData = GetValueData(dataStream);

        Log.Verbose("{This}",this);
    }

    public string ValueData { get; }
    public ValueType ValueDataType { get; }

    public long RecordPosition { get; }
    public long Size { get; }

    public string AsXml(List<SubstitutionArrayEntry> substitutionEntries, long parentOffset)
    {
        return ValueData;
    }

    public TagBuilder.BinaryTag TagType => TagBuilder.BinaryTag.Value;

    private string GetValueData(BinaryReader dataStream)
    {
        if (Size < 0)
        {
            throw new InvalidDataException($"Invalid value size {Size} for type {ValueDataType}");
        }

        var sizeBytes = (int) Size;

        switch (ValueDataType)
        {
            case TagBuilder.ValueType.StringType:
                return Encoding.Unicode.GetString(dataStream.ReadBytes(sizeBytes * 2)).Trim('\0');
            case TagBuilder.ValueType.AnsiStringType:
                return Encoding.ASCII.GetString(dataStream.ReadBytes(sizeBytes)).Trim('\0');
            case TagBuilder.ValueType.Int8Type:
                return ((sbyte) dataStream.ReadByte()).ToString();
            case TagBuilder.ValueType.UInt8Type:
                return dataStream.ReadByte().ToString();
            case TagBuilder.ValueType.Int16Type:
                return dataStream.ReadInt16().ToString();
            case TagBuilder.ValueType.UInt16Type:
                return dataStream.ReadUInt16().ToString();
            case TagBuilder.ValueType.Int32Type:
                return dataStream.ReadInt32().ToString();
            case TagBuilder.ValueType.UInt32Type:
                return dataStream.ReadUInt32().ToString();
            case TagBuilder.ValueType.Int64Type:
                return dataStream.ReadInt64().ToString();
            case TagBuilder.ValueType.UInt64Type:
                return dataStream.ReadUInt64().ToString();
            case TagBuilder.ValueType.Real32Type:
                return dataStream.ReadSingle().ToString(CultureInfo.InvariantCulture);
            case TagBuilder.ValueType.Real64Type:
                return dataStream.ReadDouble().ToString(CultureInfo.InvariantCulture);
            case TagBuilder.ValueType.BoolType:
                return (dataStream.ReadInt32() != 0).ToString();
            case TagBuilder.ValueType.GuidType:
                return new Guid(dataStream.ReadBytes(16)).ToString();
            case TagBuilder.ValueType.BinaryType:
                return BitConverter.ToString(dataStream.ReadBytes(sizeBytes));
            default:
                return BitConverter.ToString(dataStream.ReadBytes(sizeBytes));
        }
    }

    public override string ToString()
    {
        return $"Type: {ValueDataType}, Value Data: {ValueData}";
    }
}