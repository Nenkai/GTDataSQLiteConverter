using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using Syroot.BinaryData.Memory;
using Syroot.BinaryData;

using GTDataSQLiteConverter.Entities;
using GTDataSQLiteConverter.Formats;
using System.Xml.Linq;

namespace GTDataSQLiteConverter
{
    public class SQLiteExporter
    {
        private CarDataBase _database;

        private SqliteConnection _con;

        public SQLiteExporter(CarDataBase database)
        {
            _database = database;
        }

        public void ExportTables(string sqliteDbFile)
        {
            _con = new SqliteConnection($"Data Source={sqliteDbFile}");
            _con.Open();

            MakeTableInfo();

            for (int i = 0; i < _database.GetNumElements(); i++)
            {
                CarDatabaseFileType type = (CarDatabaseFileType)i;
                var table = _database.GetFile(i);
                string tableName = type.ToString();

                string headersFile = TableMappingReader.GetHeadersFile(tableName);
                if (string.IsNullOrEmpty(headersFile))
                {
                    Console.WriteLine($"Skipped '{tableName}': unmapped.");
                    continue;
                }

                Console.WriteLine($"Reading '{tableName}'.");
                var columnMappings = TableMappingReader.ReadColumnMappings(headersFile, out int readSize);
                if (table.ElementSize != readSize)
                    Console.WriteLine($"WARNING: '{tableName}' non-matching mapped size");

                var rows = ReadRows(table, columnMappings, 0);

                ExportTableToSQLite(tableName, columnMappings, rows);
            }

            _con.Close();
            _con.Dispose();
        }

        public void MakeTableInfo()
        {
            IEnumerable<DataBlock> elems = _database.GetElements().OrderBy(e => e.TableID);

            var command = _con.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS _DatabaseTableInfo;";
            command.ExecuteNonQuery();

            command = _con.CreateCommand();
            command.CommandText = "CREATE TABLE _DatabaseTableInfo (TableName TEXT, TableID INTEGER, Version INTEGER)";
            command.ExecuteNonQuery();

            foreach (DataBlock elem in elems)
            {
                command = _con.CreateCommand();
                command.CommandText = $"INSERT INTO  _DatabaseTableInfo (TableName, TableID, Version) VALUES (\"{((CarDatabaseFileType)elem.TableID)}\", {elem.TableID}, {elem.Version})";
                command.ExecuteNonQuery();
            }
        }

        private List<TableRow> ReadRows(DataBlock dataBlock, List<TableColumn> columns, int rowLength)
        {
            var rows = new List<TableRow>();
            for (var i = 0; i < dataBlock.NumOfElements; i++)
            {
                TableRow row = new TableRow();

                Span<byte> rowData = dataBlock.GetEntry(i);
                SpanReader sr = new SpanReader(rowData);

                for (int j = 0; j < columns.Count; j++)
                {
                    TableColumn col = columns[j];
                    sr.Position = (int)col.Offset;

                    switch (col.Type)
                    {
                        case DBColumnType.Id:
                            {
                                ulong hash = sr.ReadUInt64();
                                if (hash == 0)
                                    row.Cells.Add(null);
                                else
                                {
                                    string str = _database.GetIDString(hash);
                                    row.Cells.Add(str);
                                }
                            }
                            break;
                        case DBColumnType.String:
                            {
                                string str = _database.GetString(sr.ReadInt16());
                                row.Cells.Add(str);
                            }
                            break;
                        case DBColumnType.Unicode:
                            {
                                string str = _database.GetMultiByteString(sr.ReadInt16());
                                row.Cells.Add(str);
                            }
                            break;
                        case DBColumnType.Int64:
                            row.Cells.Add(sr.ReadUInt64());
                            break;
                        case DBColumnType.Int:
                            row.Cells.Add(sr.ReadUInt32());
                            break;
                        case DBColumnType.Float:
                            row.Cells.Add(sr.ReadSingle());
                            break;
                        case DBColumnType.Double:
                            row.Cells.Add(sr.ReadDouble());
                            break;
                        case DBColumnType.Byte:
                            row.Cells.Add(sr.ReadByte());
                            break;
                        case DBColumnType.Short:
                            row.Cells.Add(sr.ReadUInt16());
                            break;

                        default:
                            break;
                    }
                    
                }

                rows.Add(row);
            }

            return rows;
        }

        public void ExportTableToSQLite(string name, List<TableColumn> columns, List<TableRow> rows)
        {
            //SQL: DROP TABLE IF EXISTS
            var command = _con.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS \"{name}\";";

#if DONT_RUN_SQL
            Console.WriteLine($"Skipping DROP TABLE IF EXISTS for '{name}'.");
            command.Cancel();
#else
            Console.WriteLine($"Running DROP TABLE IF EXISTS for '{name}'.");
            command.ExecuteNonQuery();
#endif


            //SQL: CREATE TABLE
            string tableDefinition = $"CREATE TABLE \"{name}\" (\n";
            foreach (TableColumn column in columns)
            {
                tableDefinition += $"    \"{column.Name}\" {DBUtils.TypeToSQLiteTypeName(column.Type)},\n";
            }

            tableDefinition = tableDefinition.Remove(tableDefinition.Length - 2); // replace trailing comma
            tableDefinition += "\n";
            tableDefinition += ");";

#if PRINT_SQL_QUERIES
            Console.WriteLine(tableDefinition);
#endif
            command = _con.CreateCommand();
            command.CommandText = tableDefinition;
#if DONT_RUN_SQL
            Console.WriteLine($"Skipping CREATE TABLE for '{name}'.");
            command.Cancel();
#else
            Console.WriteLine($"Running CREATE TABLE for '{name}'.");
            command.ExecuteNonQuery();
#endif

            //SQL: INSERT INTO
            if (rows.Count > 0)
            {
                string insertDefinition = $"INSERT INTO \"{name}\" (";
                foreach (TableColumn header in columns)
                {
                    insertDefinition += $"\"{header.Name}\", ";
                }
                insertDefinition = insertDefinition.Remove(insertDefinition.Length - 2); // replace trailing comma
                insertDefinition += ")\n" +
                                    "VALUES\n";

                for (int entryCounter = 0; entryCounter < rows.Count; entryCounter++)
                {
                    insertDefinition += "    (";
                    var row = rows[entryCounter];

                    for (int i = 0; i < columns.Count; i++)
                    {
                        TableColumn column = columns[i];

                        insertDefinition += column.Type switch
                        {
                            DBColumnType.String => $"'{((string)row.Cells[i]).Replace("'", "''")}', ",
                            DBColumnType.Unicode => $"'{((string)row.Cells[i]).Replace("'", "''")}', ",
                            DBColumnType.Id => row.Cells[i] is not null ? $"'{((string)row.Cells[i]).Replace("'", "''")}', " : "NULL, ",
                            DBColumnType.Int => $"{(uint)row.Cells[i]}, ",
                            DBColumnType.Float => $"{(float)row.Cells[i]}, ",
                            DBColumnType.Int64 => $"{(ulong)row.Cells[i]}, ",
                            DBColumnType.Short => $"{(ushort)row.Cells[i]}, ",
                            DBColumnType.Byte => $"{(byte)row.Cells[i]}, ",
                            DBColumnType.Double => $"{(double)row.Cells[i]}, ",
                            _ => throw new InvalidDataException($"Unexpected type '{column.Type}' for column {column.Name} in table {name}")
                        };
                    }

                    insertDefinition = insertDefinition.Remove(insertDefinition.Length - 2); // replace trailing comma
                    insertDefinition += "),\n";

                    if (entryCounter % 100 == 99 && entryCounter < rows.Count - 25)
                    {
                        insertDefinition = insertDefinition.Remove(insertDefinition.Length - 2); // replace trailing comma
                        insertDefinition += ";\n";

#if PRINT_SQL_QUERIES
                        Console.WriteLine(insertDefinition);
#endif

                        command = _con.CreateCommand();
                        command.CommandText = insertDefinition;
#if DONT_RUN_SQL
                    Console.WriteLine($"Skipping early ({(100.0f * entryCounter) / (1.0f * rows.Count)}%) INSERT INTO for '{name}'.");
                    command.Cancel();
#else
                        Console.WriteLine($"Running early ({(100.0f * entryCounter) / (1.0f * rows.Count)}%) INSERT INTO for '{name}'.");
                        command.ExecuteNonQuery();
#endif

                        insertDefinition = $"INSERT INTO \"{name}\" (";
                        foreach (TableColumn column in columns)
                        {
                            insertDefinition += $"\"{column.Name}\", ";
                        }
                        insertDefinition = insertDefinition.Remove(insertDefinition.Length - 2); // replace trailing comma
                        insertDefinition += ")\n" +
                                            "VALUES\n";
                    }
                }

                insertDefinition = insertDefinition.Remove(insertDefinition.Length - 2); // replace trailing comma
                insertDefinition += ";\n";

                /*
                var errorValue = ((stringDataOffset == 0 ? bs.Length : stringDataOffset) - bs.Position) / entryCount;
                if (errorValue > 0)
                {
                    Console.WriteLine($"Warning: Headers for table {name} are incorrect.\n" +
                                $" Entries are {errorValue} byte(s) too large.");
                }
                else if (errorValue < 0)
                {
                    Console.WriteLine($"Warning: Headers for table {name} are incorrect.\n" +
                                $" Entries are {Math.Abs(errorValue)} byte(s) too small.");
                }
                */

#if PRINT_SQL_QUERIES
                Console.WriteLine(insertDefinition);
#endif

                command = _con.CreateCommand();
                command.CommandText = insertDefinition;
#if DONT_RUN_SQL
                Console.WriteLine($"Skipping INSERT INTO for '{name}'.");
                command.Cancel();
#else
                Console.WriteLine($"Running INSERT INTO for '{name}'.");
                command.ExecuteNonQuery();
#endif
            }
            Console.WriteLine($"All done for '{name}'.");
        }  
    }
}
