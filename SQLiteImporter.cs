using Microsoft.Data.Sqlite;
using Syroot.BinaryData;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Syroot.BinaryData.Memory;
using GTDataSQLiteConverter.Entities;
using System.Data;
using System.Drawing;
using System.Runtime.InteropServices;

namespace GTDataSQLiteConverter
{
    public class SQLiteImporter
    {
        private string _sqliteFile;

        private SqliteConnection _con;
        private CarDataBase _database;

        public Dictionary<string, DataBlock> _tableNameToTable = new Dictionary<string, DataBlock>();

        public SQLiteImporter(string sqliteFile)
        {
            _sqliteFile = sqliteFile;
            _database = new CarDataBase();
        }

        public void Import(string outputDir, string suffix)
        {
            _con = new SqliteConnection($"Data Source={_sqliteFile}");
            _con.Open();

            CreateTables();
            FillTables();
            Serialize(outputDir, suffix);
        }


        public void CreateTables()
        {
            var command = _con.CreateCommand();
            command.CommandText = $"SELECT * FROM _DatabaseTableInfo ORDER BY TableID;";
            var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string tableName = (string)reader["TableName"];
                long tableId = (long)reader["TableID"];
                long version = (long)reader["Version"];

                var block = new DataBlock() { TableID = (short)tableId, Version = (ushort)version };
                _database.Elements.Insert((int)tableId, block);
                _tableNameToTable.Add(tableName, block);
            }
        }

        public void FillTables()
        {
            foreach (KeyValuePair<string, DataBlock> table in _tableNameToTable)
            {
                var headerFileName = TableMappingReader.GetHeadersFile(table.Key);
                if (string.IsNullOrEmpty(headerFileName))
                    continue;

                var columns = TableMappingReader.ReadColumnMappings(headerFileName, out int rowLength);

                var command = _con.CreateCommand();
                command.CommandText = $"SELECT * FROM {table.Key};";
                var reader = command.ExecuteReader();

                if (columns.Count != reader.FieldCount)
                    throw new InvalidDataException($"Mismatched amount of columns for table {table.Key}");

                Console.WriteLine($"Reading from {table.Key}...");
                List<TableRow> rows = new List<TableRow>();
                while (reader.Read())
                {
                    var row = new TableRow();

                    for (int i = 0; i < columns.Count; i++)
                    {
                        switch (columns[i].Type)
                        {
                            case DBColumnType.Id:
                                {
                                    if (reader.IsDBNull(i))
                                    {
                                        row.Cells.Add(null);
                                    }
                                    else
                                    {
                                        string id = reader.GetString(i);
                                        ulong hash = HashString(id);

                                        ushort strIndex = _database.IDStringDataBase.Add(id);
                                        _database.IDTable.Add(hash, strIndex);

                                        row.Cells.Add(hash);
                                    }
                                }
                                break;
                            case DBColumnType.Unicode:
                                {
                                    string str = reader.GetString(i);
                                    ushort id = _database.UnicodeStringDataBase.Add(str);
                                    row.Cells.Add(id);
                                }
                                break;
                            case DBColumnType.String:
                                {
                                    string str = reader.GetString(i);
                                    ushort id = _database.StringDataBase.Add(str);
                                    row.Cells.Add(id);
                                }
                                break;
                            case DBColumnType.Float:
                                {
                                    float f = reader.GetFloat(i);
                                    row.Cells.Add(f);
                                }
                                break;
                            case DBColumnType.Int:
                                {
                                    int value = (int)reader.GetInt64(i);
                                    row.Cells.Add(value);
                                }
                                break;
                            case DBColumnType.Int64:
                                {
                                    long value = reader.GetInt64(i);
                                    row.Cells.Add(value);
                                }
                                break;
                            case DBColumnType.Short:
                                {
                                    short value = (short)reader.GetInt64(i);
                                    row.Cells.Add(value);
                                }
                                break;
                            case DBColumnType.Byte:
                                {
                                    byte value = reader.GetByte(i);
                                    row.Cells.Add(value);
                                }
                                break;
                            default:
                                throw new Exception("Unexpected type");
                                break;
                        }
                    }

                    rows.Add(row);
                }

                Console.WriteLine($"Serializing {table.Key} ({rows.Count} rows)...");

                // Sort for bsearch (mandatory)
                rows.Sort((a, b) => ((ulong)a.Cells[0]).CompareTo((ulong)b.Cells[0]));

                // Write rows & set fields
                var block = table.Value;
                block.Buffer = new byte[rowLength * rows.Count];
                block.ElementSize = (ushort)rowLength;
                block.NumOfElements = (ushort)rows.Count;

                SpanWriter sw = new SpanWriter(table.Value.Buffer);
                for (int i = 0; i < rows.Count; i++)
                {
                    TableRow row = rows[i];
                    for (int j = 0; j < columns.Count; j++)
                    {
                        sw.Position = (int)((i * rowLength) + columns[j].Offset);
                        switch (columns[j].Type)
                        {
                            case DBColumnType.Id:
                                sw.WriteUInt64((ulong)(row.Cells[j] ?? 0UL));
                                break;
                            case DBColumnType.Unicode:
                            case DBColumnType.String:
                                sw.WriteUInt16((ushort)row.Cells[j]);
                                break;
                            case DBColumnType.Float:
                                sw.WriteSingle((float)row.Cells[j]);
                                break;
                            case DBColumnType.Int:
                                sw.WriteInt32((int)row.Cells[j]);
                                break;
                            case DBColumnType.Int64:
                                sw.WriteInt64((long)row.Cells[j]);
                                break;
                            case DBColumnType.Short:
                                sw.WriteInt16((short)row.Cells[j]);
                                break;
                            case DBColumnType.Byte:
                                sw.WriteByte((byte)row.Cells[j]);
                                break;
                            default:
                                throw new Exception("Unexpected type");
                                break;
                        }
                    }
                }
            }
        }

        public void Serialize(string outputDir, string suffix)
        {
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            _database.Save(outputDir, suffix);
        }

        public static ulong HashString(string name)
        {
            ulong hash = 0;
            char[] nameChars = name.ToCharArray();

            foreach (char nameChar in nameChars)
            {
                hash += (byte)nameChar;
            }

            foreach (char nameChar in nameChars)
            {
                byte asciiValue = (byte)nameChar;
                ulong temp1 = hash << 7;
                ulong temp2 = hash >> 57;
                hash = temp1 | temp2;
                hash += asciiValue;
            }

            return hash;
        }
    }
}
