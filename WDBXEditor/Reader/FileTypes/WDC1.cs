﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WDBXEditor.Storage;
using static WDBXEditor.Common.Constants;

namespace WDBXEditor.Reader.FileTypes
{
	public class WDC1 : WDB6
	{
		public int PackedDataOffset;
		public uint RelationshipCount;
		public int OffsetTableOffset;
		public int IndexSize;
		public int ColumnMetadataSize;
		public int SparseDataSize;
		public int PalletDataSize;
		public int RelationshipDataSize;

		public List<ColumnStructureEntry> ColumnMeta;
		public RelationShipData RelationShipData;
		public Dictionary<int, MinMax> MinMaxValues;

		private byte[] recordData;

		#region Read
		public override void ReadHeader(ref BinaryReader dbReader, string signature)
		{
			ReadBaseHeader(ref dbReader, signature);

			TableHash = dbReader.ReadUInt32();
			LayoutHash = dbReader.ReadInt32();
			MinId = dbReader.ReadInt32();
			MaxId = dbReader.ReadInt32();
			Locale = dbReader.ReadInt32();
			CopyTableSize = dbReader.ReadInt32();
			Flags = (HeaderFlags)dbReader.ReadUInt16();
			IdIndex = dbReader.ReadUInt16();
			TotalFieldSize = dbReader.ReadUInt32();

			PackedDataOffset = dbReader.ReadInt32();
			RelationshipCount = dbReader.ReadUInt32();
			OffsetTableOffset = dbReader.ReadInt32();
			IndexSize = dbReader.ReadInt32();
			ColumnMetadataSize = dbReader.ReadInt32();
			SparseDataSize = dbReader.ReadInt32();
			PalletDataSize = dbReader.ReadInt32();
			RelationshipDataSize = dbReader.ReadInt32();

			//Gather field structures
			FieldStructure = new List<FieldStructureEntry>();
			for (int i = 0; i < FieldCount; i++)
			{
				var field = new FieldStructureEntry(dbReader.ReadInt16(), dbReader.ReadUInt16());
				FieldStructure.Add(field);
			}

			recordData = dbReader.ReadBytes((int)(RecordCount * RecordSize));
			Array.Resize(ref recordData, recordData.Length + 8);
		}

		public new Dictionary<int, byte[]> ReadOffsetData(BinaryReader dbReader, long pos)
		{
			Dictionary<int, byte[]> CopyTable = new Dictionary<int, byte[]>();
			List<Tuple<int, short>> offsetmap = new List<Tuple<int, short>>();
			Dictionary<int, OffsetDuplicate> firstindex = new Dictionary<int, OffsetDuplicate>();
			Dictionary<int, int> OffsetDuplicates = new Dictionary<int, int>();
			Dictionary<int, List<int>> Copies = new Dictionary<int, List<int>>();

			int[] m_indexes = null;

			// OffsetTable
			if (HasOffsetTable && OffsetTableOffset > 0)
			{
				dbReader.BaseStream.Position = OffsetTableOffset;
				for (int i = 0; i < (MaxId - MinId + 1); i++)
				{
					int offset = dbReader.ReadInt32();
					short length = dbReader.ReadInt16();

					if (offset == 0 || length == 0)
						continue;

					// special case, may contain duplicates in the offset map that we don't want
					if (CopyTableSize == 0)
					{
						if (!firstindex.ContainsKey(offset))
						{
							firstindex.Add(offset, new OffsetDuplicate(offsetmap.Count, firstindex.Count));
						}							
						else
						{
							OffsetDuplicates.Add(MinId + i, firstindex[offset].VisibleIndex);
							continue;
						}
					}

					offsetmap.Add(new Tuple<int, short>(offset, length));
				}
			}

			// IndexTable
			if (HasIndexTable)
			{
				m_indexes = new int[RecordCount];
				for (int i = 0; i < RecordCount; i++)
					m_indexes[i] = dbReader.ReadInt32();
			}

			// Copytable
			if (CopyTableSize > 0)
			{
				long end = dbReader.BaseStream.Position + CopyTableSize;
				while (dbReader.BaseStream.Position < end)
				{
					int id = dbReader.ReadInt32();
					int idcopy = dbReader.ReadInt32();

					if (!Copies.ContainsKey(idcopy))
						Copies.Add(idcopy, new List<int>());

					Copies[idcopy].Add(id);
				}
			}

			// ColumnMeta
			ColumnMeta = new List<ColumnStructureEntry>();
			for (int i = 0; i < FieldCount; i++)
			{
				var column = new ColumnStructureEntry()
				{
					RecordOffset = dbReader.ReadUInt16(),
					Size = dbReader.ReadUInt16(),
					AdditionalDataSize = dbReader.ReadUInt32(), // size of pallet / sparse values
					CompressionType = (CompressionType)dbReader.ReadUInt32(),
					BitOffset = dbReader.ReadInt32(),
					BitWidth = dbReader.ReadInt32(),
					Cardinality = dbReader.ReadInt32()
				};

				// preload arraysizes
				if (column.CompressionType == CompressionType.None)
					column.ArraySize = Math.Max(column.Size / FieldStructure[i].BitCount, 1);
				else if (column.CompressionType == CompressionType.PalletArray)
					column.ArraySize = Math.Max(column.Cardinality, 1);

				ColumnMeta.Add(column);
			}

			// Pallet values
			for (int i = 0; i < ColumnMeta.Count; i++)
			{
				if (ColumnMeta[i].CompressionType == CompressionType.Pallet || ColumnMeta[i].CompressionType == CompressionType.PalletArray)
				{
					int elements = (int)ColumnMeta[i].AdditionalDataSize / 4;
					int cardinality = Math.Max(ColumnMeta[i].Cardinality, 1);

					ColumnMeta[i].PalletValues = new List<byte[]>();
					for (int j = 0; j < elements / cardinality; j++)
						ColumnMeta[i].PalletValues.Add(dbReader.ReadBytes(cardinality * 4));
				}
			}

			// Sparse values
			for (int i = 0; i < ColumnMeta.Count; i++)
			{
				if (ColumnMeta[i].CompressionType == CompressionType.Sparse)
				{
					ColumnMeta[i].SparseValues = new Dictionary<int, byte[]>();
					for (int j = 0; j < ColumnMeta[i].AdditionalDataSize / 8; j++)
						ColumnMeta[i].SparseValues[dbReader.ReadInt32()] = dbReader.ReadBytes(4);
				}
			}

			// Relationships
			if (RelationshipDataSize > 0)
			{
				RelationShipData = new RelationShipData()
				{
					Records = dbReader.ReadUInt32(),
					MinId = dbReader.ReadUInt32(),
					MaxId = dbReader.ReadUInt32(),
					Entries = new Dictionary<uint, byte[]>()
				};

				for (int i = 0; i < RelationShipData.Records; i++)
				{
					byte[] foreignKey = dbReader.ReadBytes(4);
					uint index = dbReader.ReadUInt32();
					// has duplicates just like the copy table does... why?
					if (!RelationShipData.Entries.ContainsKey(index))
						RelationShipData.Entries.Add(index, foreignKey);
				}

				FieldStructure.Add(new FieldStructureEntry(0, 0));
				ColumnMeta.Add(new ColumnStructureEntry());
			}

			// Record Data
			BitStream bitStream = new BitStream(recordData);
			for (int i = 0; i < RecordCount; i++)
			{
				int id = 0;

				if (HasOffsetTable && HasIndexTable)
				{
					id = m_indexes[CopyTable.Count];
					var map = offsetmap[i];

					if (CopyTableSize == 0 && firstindex[map.Item1].HiddenIndex != i) //Ignore duplicates
						continue;

					dbReader.BaseStream.Position = map.Item1;

					byte[] data = dbReader.ReadBytes(map.Item2);

					IEnumerable<byte> recordbytes = BitConverter.GetBytes(id).Concat(data);

					// append relationship id
					if (RelationShipData != null)
					{
						// seen cases of missing indicies 
						if (RelationShipData.Entries.TryGetValue((uint)i, out byte[] foreignData))
							recordbytes = recordbytes.Concat(foreignData);
						else
							recordbytes = recordbytes.Concat(new byte[4]);
					}

					CopyTable.Add(id, recordbytes.ToArray());

					if (Copies.ContainsKey(id))
					{
						foreach (int copy in Copies[id])
							CopyTable.Add(copy, BitConverter.GetBytes(copy).Concat(data).ToArray());
					}
				}
				else
				{
					bitStream.Seek(i * RecordSize, 0);
					int idOffset = 0;

					List<byte> data = new List<byte>();

					if (HasIndexTable)
					{
						id = m_indexes[i];
						data.AddRange(BitConverter.GetBytes(id));
					}

					for (int f = 0; f < FieldCount; f++)
					{
						int bitOffset = ColumnMeta[f].BitOffset;
						int bitWidth = ColumnMeta[f].BitWidth;
						int cardinality = ColumnMeta[f].Cardinality;
						uint palletIndex;

						switch (ColumnMeta[f].CompressionType)
						{
							case CompressionType.None:
								int bitSize = FieldStructure[f].BitCount;
								if (!HasIndexTable && f == IdIndex)
								{
									idOffset = data.Count;
									id = (int)bitStream.ReadUInt32(bitSize); // always read Ids as ints
									data.AddRange(BitConverter.GetBytes(id));
								}
								else
								{
									for (int x = 0; x < ColumnMeta[f].ArraySize; x++)
										data.AddRange(bitStream.ReadBytesPadded(bitSize));
								}
								break;

							case CompressionType.Immediate:
								if (!HasIndexTable && f == IdIndex)
								{
									idOffset = data.Count;
									id = (int)bitStream.ReadUInt32(bitWidth); // always read Ids as ints
									data.AddRange(BitConverter.GetBytes(id));
									continue;
								}
								else
								{
									data.AddRange(bitStream.ReadBytesPadded(bitWidth));
								}
								break;

							case CompressionType.Sparse:
								if (ColumnMeta[f].SparseValues.TryGetValue(id, out byte[] valBytes))
									data.AddRange(valBytes);
								else
									data.AddRange(BitConverter.GetBytes(ColumnMeta[f].BitOffset));
								break;

							case CompressionType.Pallet:
							case CompressionType.PalletArray:
								palletIndex = bitStream.ReadUInt32(bitWidth);
								data.AddRange(ColumnMeta[f].PalletValues[(int)palletIndex]);
								break;

							default:
								throw new Exception($"Unknown compression {ColumnMeta[f].CompressionType}");

						}
					}

					// append relationship id
					if (RelationShipData != null)
					{
						// seen cases of missing indicies 
						if (RelationShipData.Entries.TryGetValue((uint)i, out byte[] foreignData))
							data.AddRange(foreignData);
						else
							data.AddRange(new byte[4]);
					}

					CopyTable.Add(id, data.ToArray());

					if (Copies.ContainsKey(id))
					{
						foreach (int copy in Copies[id])
						{
							byte[] newrecord = CopyTable[id].ToArray();
							Buffer.BlockCopy(BitConverter.GetBytes(copy), 0, newrecord, idOffset, 4);
							CopyTable.Add(copy, newrecord);
						}
					}
				}
			}

			if (HasIndexTable)
			{
				FieldStructure.Insert(0, new FieldStructureEntry(0, 0));
				ColumnMeta.Insert(0, new ColumnStructureEntry());
			}
			
			offsetmap.Clear();
			firstindex.Clear();
			OffsetDuplicates.Clear();
			Copies.Clear();
			Array.Resize(ref recordData, 0);
			bitStream.Dispose();
			ColumnMeta.ForEach(x => { x.PalletValues?.Clear(); x.SparseValues?.Clear(); });

			InternalRecordSize = (uint)CopyTable.First().Value.Length;

			return CopyTable;
		}

		public override byte[] ReadData(BinaryReader dbReader, long pos)
		{
			Dictionary<int, byte[]> CopyTable = ReadOffsetData(dbReader, pos);
			OffsetLengths = CopyTable.Select(x => x.Value.Length).ToArray();
			return CopyTable.Values.SelectMany(x => x).ToArray();
		}


		public void SetColumnMinMaxValues(DBEntry entry)
		{
			MinMaxValues = new Dictionary<int, MinMax>();
			int column = 0;
			for (int i = 0; i < ColumnMeta.Count; i++)
			{
				// get the column type - skip strings
				var type = entry.Data.Columns[column].DataType;
				if (type == typeof(string))
				{
					column += ColumnMeta[i].ArraySize;
					continue;
				}

				int bits = ColumnMeta[i].CompressionType == CompressionType.None ? FieldStructure[i].BitCount : ColumnMeta[i].BitWidth;
				if ((bits & (bits - 1)) == 0 && bits >= 8) // power of two and >= sizeof(byte) means a standard type
				{
					column += ColumnMeta[i].ArraySize;
					continue;
				}

				// calculate the min and max values
				bool signed = Convert.ToBoolean(type.GetField("MinValue").GetValue(null));
				bool isfloat = type == typeof(float);

				//bool metaSigned = ColumnMeta[i].CompressionType == CompressionType.Immediate && (ColumnMeta[i].Cardinality & 1) == 1;
				//if (ColumnMeta[i].CompressionType == CompressionType.Immediate && metaSigned != signed && i != IdIndex && !isfloat)
				//	throw new Exception($"Invalid sign for column {i}");

				object max = signed ? long.MaxValue >> (64 - bits) : (object)(ulong.MaxValue >> (64 - bits));
				object min = signed ? long.MinValue >> (64 - bits) : 0;
				if (isfloat)
				{
					max = BitConverter.ToSingle(BitConverter.GetBytes((dynamic)max), 0);
					min = BitConverter.ToSingle(BitConverter.GetBytes((dynamic)min), 0);
				}

				for (int j = 0; j < ColumnMeta[i].ArraySize; j++)
				{
					entry.Data.Columns[column].ExtendedProperties.Add("MaxValue", max);
					if (signed || isfloat)
						entry.Data.Columns[column].ExtendedProperties.Add("MinValue", min);

					MinMax minmax = new MinMax()
					{
						Signed = signed,
						MaxVal = max,
						MinVal = min,
						IsSingle = isfloat
					};

					MinMaxValues[column] = minmax;
					column++;
				}
			}
		}

		public void AddRelationshipColumn(DBEntry entry)
		{
			if (RelationShipData == null)
				return;

			if (!entry.Data.Columns.Cast<DataColumn>().Any(x => x.ExtendedProperties.ContainsKey("RELATIONSHIP")))
			{
				DataColumn dataColumn = new DataColumn("RelationshipData", typeof(uint));
				dataColumn.ExtendedProperties.Add("RELATIONSHIP", true);
				entry.Data.Columns.Add(dataColumn);
			}
		}
		#endregion


		#region Write

		public override void WriteHeader(BinaryWriter bw, DBEntry entry)
		{
			Tuple<int, int> minmax = entry.MinMax();
			bw.BaseStream.Position = 0;

			WriteBaseHeader(bw, entry);

			bw.Write((int)TableHash);
			bw.Write(LayoutHash);
			bw.Write(minmax.Item1); //MinId
			bw.Write(minmax.Item2); //MaxId
			bw.Write(Locale);
			bw.Write(0); //CopyTableSize
			bw.Write((ushort)Flags); //Flags
			bw.Write(IdIndex); //IdColumn
			bw.Write(TotalFieldSize);

			bw.Write(PackedDataOffset);
			bw.Write(RelationshipCount);
			bw.Write(0);  // OffsetTableOffset
			bw.Write(0);  // IndexSize
			bw.Write(0);  // ColumnMetadataSize
			bw.Write(0);  // SparseDataSize
			bw.Write(0);  // PalletDataSize
			bw.Write(0);  // RelationshipDataSize

			// Write the field_structure bits
			for (int i = 0; i < FieldStructure.Count; i++)
			{
				if (HasIndexTable && i == 0) continue;
				if (RelationShipData != null && i == FieldStructure.Count - 1) continue;

				bw.Write(FieldStructure[i].Bits);
				bw.Write(FieldStructure[i].Offset);
			}

			WriteData(bw, entry);
		}

		/// <summary>
		/// WDC1 writing is entirely different so has been moved to inside the class.
		/// Will work on inheritence when WDC2 comes along - can't wait...
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="entry"></param>

		public void WriteData(BinaryWriter bw, DBEntry entry)
		{
			List<Tuple<int, short>> offsetMap = new List<Tuple<int, short>>();
			StringTable stringTable = new StringTable(true);
			bool IsSparse = HasIndexTable && HasOffsetTable;
			Dictionary<int, List<int>> copyRecords = new Dictionary<int, List<int>>();
			List<int> copyIds = new List<int>();

			long pos = bw.BaseStream.Position;

			// get a list of identical records			
			if (CopyTableSize > 0)
			{
				var copies = entry.GetCopyRows();
				foreach (var c in copies)
				{
					copyRecords.Add(c.First(), c.Skip(1).ToList());
					copyIds.AddRange(c.Skip(1));
				}
			}

			// get relationship data
			DataColumn relationshipColumn = entry.Data.Columns.Cast<DataColumn>().FirstOrDefault(x => x.ExtendedProperties.ContainsKey("RELATIONSHIP"));
			if (relationshipColumn != null)
			{
				int index = entry.Data.Columns.IndexOf(relationshipColumn);

				Dictionary<int, uint> relationData = new Dictionary<int, uint>();
				foreach (DataRow r in entry.Data.Rows)
				{
					int id = r.Field<int>(entry.Key);
					if (!copyIds.Contains(id))
						relationData.Add(id, r.Field<uint>(index));
				}

				RelationShipData = new RelationShipData()
				{
					Records = (uint)relationData.Count,
					MinId = relationData.Values.Min(),
					MaxId = relationData.Values.Max(),
					Entries = relationData.Values.Select((x, i) => new { Index = (uint)i, Id = x }).ToDictionary(x => x.Index, x => BitConverter.GetBytes(x.Id))
				};

				relationData.Clear();
			}

			// temporarily remove the fake records
			if (HasIndexTable)
			{
				FieldStructure.RemoveAt(0);
				ColumnMeta.RemoveAt(0);
			}
			if (RelationShipData != null)
			{
				FieldStructure.RemoveAt(FieldStructure.Count - 1);
				ColumnMeta.RemoveAt(ColumnMeta.Count - 1);
			}

			// remove any existing column values
			ColumnMeta.ForEach(x => { x.PalletValues?.Clear(); x.SparseValues?.Clear(); });

			// RecordData - this can still all be done via one function, except inline strings
			BitStream bitStream = new BitStream(entry.Data.Rows.Count * ColumnMeta.Max(x => x.RecordOffset));
			for (int rowIndex = 0; rowIndex < entry.Data.Rows.Count; rowIndex++)
			{
				Queue<object> rowData = new Queue<object>(entry.Data.Rows[rowIndex].ItemArray);

				int id = entry.Data.Rows[rowIndex].Field<int>(entry.Key);

				if (CopyTableSize > 0) // skip copy records
				{
					if (copyIds.Contains(id))
						continue;
				}

				if (HasIndexTable) // dump the id from the row data
					rowData.Dequeue();

				bitStream.SeekNextOffset(); // each row starts at a 0 bit position

				long offset = pos + bitStream.Offset; // used for offset map calcs

				for (int fieldIndex = 0; fieldIndex < FieldCount; fieldIndex++)
				{
					int bitWidth = ColumnMeta[fieldIndex].BitWidth;
					int bitSize = FieldStructure[fieldIndex].BitCount;
					int arraySize = ColumnMeta[fieldIndex].ArraySize;

					// get the values for the current record, array size may require more than 1
					object[] values = ExtractFields(rowData, stringTable, bitStream, fieldIndex);
					byte[][] data = values.Select(x => (byte[])BitConverter.GetBytes((dynamic)x)).ToArray(); // shameful hack
					if (data.Length == 0)
						continue;

					switch (ColumnMeta[fieldIndex].CompressionType)
					{
						case CompressionType.None:
							for (int i = 0; i < arraySize; i++)
								bitStream.WriteBits(data[i], bitSize);
							break;

						case CompressionType.Immediate:
							bitStream.WriteBits(data[0], bitWidth);
							break;

						case CompressionType.Sparse:
							{
								Array.Resize(ref data[0], 4);
								if (BitConverter.ToInt32(data[0], 0) != ColumnMeta[fieldIndex].BitOffset)
									ColumnMeta[fieldIndex].SparseValues.Add(id, data[0]);
							}
							break;

						case CompressionType.Pallet:
						case CompressionType.PalletArray:
							{
								byte[] combined = data.SelectMany(x => x.Concat(new byte[4]).Take(4)).ToArray(); // enforce int size rule

								int index = ColumnMeta[fieldIndex].PalletValues.FindIndex(x => x.SequenceEqual(combined));
								if (index > -1)
								{
									bitStream.WriteUInt32((uint)index, bitWidth);
								}
								else
								{
									bitStream.WriteUInt32((uint)ColumnMeta[fieldIndex].PalletValues.Count, bitWidth);
									ColumnMeta[fieldIndex].PalletValues.Add(combined);
								}
							}
							break;

						default:
							throw new Exception("Unsupported compression type " + ColumnMeta[fieldIndex].CompressionType);

					}
				}

				bitStream.SeekNextOffset();
				short size = (short)(pos + bitStream.Offset - offset);

				if (IsSparse) // matches itemsparse padding
				{					
					int remaining = size % 8 == 0 ? 0 : 8 - (size % 8);
					if (remaining > 0)
					{
						size += (short)remaining;
						bitStream.WriteBytes(new byte[remaining], remaining);
					}

					offsetMap.Add(new Tuple<int, short>((int)offset, size));
				}
				else // needs to be padded to the record size regardless of the byte count - weird eh?
				{
					if (size < RecordSize)
						bitStream.WriteBytes(new byte[RecordSize - size], RecordSize - size);
				}
			}
			bitStream.CopyStreamTo(bw.BaseStream); // write to the filestream
			bitStream.Dispose();

			// OffsetTable / StringTable, either or
			if (IsSparse)
			{
				// OffsetTable
				OffsetTableOffset = (int)bw.BaseStream.Position;
				WriteOffsetMap(bw, entry, offsetMap);
				offsetMap.Clear();
			}
			else
			{
				// StringTable
				StringBlockSize = (uint)stringTable.Size;
				stringTable.CopyTo(bw.BaseStream);
				stringTable.Dispose();
			}

			// IndexTable
			if (HasIndexTable)
			{
				pos = bw.BaseStream.Position;
				WriteIndexTable(bw, entry);
				IndexSize = (int)(bw.BaseStream.Position - pos);
			}

			// Copytable
			if (CopyTableSize > 0)
			{
				pos = bw.BaseStream.Position;
				foreach (var c in copyRecords)
				{
					foreach (var v in c.Value)
					{
						bw.Write(v);
						bw.Write(c.Key);
					}
				}
				CopyTableSize = (int)(bw.BaseStream.Position - pos);
				copyRecords.Clear();
				copyIds.Clear();
			}

			// ColumnMeta
			pos = bw.BaseStream.Position;
			foreach (var meta in ColumnMeta)
			{
				bw.Write(meta.RecordOffset);
				bw.Write(meta.Size);

				if (meta.SparseValues != null)
					bw.Write((uint)meta.SparseValues.Count * 8); // (k<4>, v<4>)
				else if (meta.PalletValues != null)
					bw.Write((uint)meta.PalletValues.Sum(x => x.Length));
				else
					bw.WriteUInt32(0);

				bw.Write((uint)meta.CompressionType);
				bw.Write(meta.BitOffset);
				bw.Write(meta.BitWidth);
				bw.Write(meta.Cardinality);
			}
			ColumnMetadataSize = (int)(bw.BaseStream.Position - pos);

			// Pallet values
			pos = bw.BaseStream.Position;
			foreach (var meta in ColumnMeta)
			{
				if (meta.CompressionType == CompressionType.Pallet || meta.CompressionType == CompressionType.PalletArray)
					bw.WriteArray(meta.PalletValues.SelectMany(x => x).ToArray());
			}
			PalletDataSize = (int)(bw.BaseStream.Position - pos);

			// Sparse values
			pos = bw.BaseStream.Position;
			foreach (var meta in ColumnMeta)
			{
				if (meta.CompressionType == CompressionType.Sparse)
				{
					foreach (var sparse in meta.SparseValues)
					{
						bw.Write(sparse.Key);
						bw.WriteArray(sparse.Value);
					}
				}
			}
			SparseDataSize = (int)(bw.BaseStream.Position - pos);

			// Relationships
			pos = bw.BaseStream.Position;
			if (RelationShipData != null)
			{
				bw.Write(RelationShipData.Records);
				bw.Write(RelationShipData.MinId);
				bw.Write(RelationShipData.MaxId);

				foreach (var relation in RelationShipData.Entries)
				{
					bw.Write(relation.Value);
					bw.Write(relation.Key);
				}
			}
			RelationshipDataSize = (int)(bw.BaseStream.Position - pos);

			// update header fields
			bw.BaseStream.Position = 16;
			bw.Write(StringBlockSize);
			bw.BaseStream.Position = 40;
			bw.Write(CopyTableSize);
			bw.BaseStream.Position = 60;
			bw.Write(OffsetTableOffset);
			bw.Write(IndexSize);
			bw.Write(ColumnMetadataSize);
			bw.Write(SparseDataSize);
			bw.Write(PalletDataSize);
			bw.Write(RelationshipDataSize);

			// reset indextable stuff
			if (HasIndexTable)
			{
				FieldStructure.Insert(0, new FieldStructureEntry(0, 0));
				ColumnMeta.Insert(0, new ColumnStructureEntry());
			}
			if (RelationShipData != null)
			{
				FieldStructure.Add(new FieldStructureEntry(0, 0));
				ColumnMeta.Add(new ColumnStructureEntry());
			}
		}

		private object[] ExtractFields(Queue<object> rowData, StringTable stringTable, BitStream bitStream, int fieldIndex)
		{
			object[] values = Enumerable.Range(0, ColumnMeta[fieldIndex].ArraySize).Select(x => rowData.Dequeue()).ToArray();

			// deal with strings
			if (values.Any(x => x.GetType() == typeof(string)))
			{
				if (HasIndexTable && HasOffsetTable)
				{
					foreach (var s in values)
						bitStream.WriteCString((string)s);

					return new object[0];
				}
				else
				{
					for (int i = 0; i < values.Length; i++)
						values[i] = stringTable.Write((string)values[i], false, false);
				}
			}

			return values;
		}

		#endregion

	}

	public class MinMax
	{
		public object MinVal;
		public object MaxVal;
		public bool Signed;
		public bool IsSingle;
	}
}
