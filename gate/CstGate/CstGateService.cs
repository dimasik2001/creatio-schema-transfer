using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.ServiceModel;
using System.ServiceModel.Activation;
using System.ServiceModel.Web;
using System.Text;
using Common.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Terrasoft.Core;
using Terrasoft.Core.DB;
using Terrasoft.Web.Common;
#if NETSTANDARD2_0
using Terrasoft.Web.Http.Abstractions;
#endif

// Creatio registers rest/* routes only from package assemblies marked for ServiceRoutes
// (SysWorkspace loads Files\Bin\<PackageName>.dll of each package at application start).
[assembly: Terrasoft.Core.Attributes.PackageReferenceAssembly(Terrasoft.Core.Attributes.RefAssemblyMarker.ServiceRoutes)]

namespace CstGate
{
	/// <summary>
	/// SQL gateway of cst (Creatio Schema Transfer) for the table data transfer. Three POST endpoints under
	/// rest/CstGateService: Info, Query, Execute. Request body: JSON, optionally gzip-compressed (detected by the gzip
	/// magic bytes; send it as application/octet-stream). Response: JSON, gzip-compressed when the request says
	/// "gzip": true. Every call needs the CanManageSolution operation. SQL runs on the stand's own DB connection
	/// (the provider Creatio uses), outside the SQL text API switches, like an administrator's SQL console.
	/// </summary>
	[ServiceContract]
	[AspNetCompatibilityRequirements(RequirementsMode = AspNetCompatibilityRequirementsMode.Required)]
	public class CstGateService : BaseService
	{
		private static readonly ILog Log = LogManager.GetLogger("CstGate");
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static readonly PropertyInfo ConnectionProperty =
			typeof(DBExecutor).GetProperty("DBConnection", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

		public static string Version => typeof(CstGateService).Assembly.GetName().Version.ToString();

		#region Endpoints

		/// <summary>Gate version, DB engine and server, current user and whether they may use the gate.</summary>
		[OperationContract]
		[WebInvoke(Method = "POST", UriTemplate = "Info", BodyStyle = WebMessageBodyStyle.Bare)]
		public Stream Info(Stream body) {
			JObject request = ReadRequest(body);
			return Respond(request, w => {
				bool can = CanUse();
				w.WritePropertyName("ok"); w.WriteValue(true);
				w.WritePropertyName("version"); w.WriteValue(Version);
				w.WritePropertyName("runtime"); w.WriteValue(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
				w.WritePropertyName("user"); w.WriteValue(UserConnection.CurrentUser.Name);
				w.WritePropertyName("contactId"); w.WriteValue(UserConnection.CurrentUser.ContactId.ToString("D"));
				w.WritePropertyName("canUse"); w.WriteValue(can);
				if (!can) return;
				using (DBExecutor executor = UserConnection.EnsureDBConnection()) {
					DbConnection connection = GetConnection(executor);
					w.WritePropertyName("engine"); w.WriteValue(EngineOf(connection));
					w.WritePropertyName("provider"); w.WriteValue(connection.GetType().FullName);
					w.WritePropertyName("serverVersion"); w.WriteValue(connection.ServerVersion);
					w.WritePropertyName("database"); w.WriteValue(connection.Database);
				}
			});
		}

		/// <summary>
		/// Runs one SELECT and returns its rows: {"sql", "params"?, "maxRows"?, "timeout"? (s), "gzip"?} →
		/// {"ok", "columns":[{"name","type"}], "rows":[[...]], "truncated", "ms"}. Types: guid, string, int, long,
		/// decimal (string), double, bool, datetime (ISO, no zone), binary (base64), other (string).
		/// </summary>
		[OperationContract]
		[WebInvoke(Method = "POST", UriTemplate = "Query", BodyStyle = WebMessageBodyStyle.Bare)]
		public Stream Query(Stream body) {
			JObject request = ReadRequest(body);
			return Respond(request, w => {
				if (!Allowed(w)) return;
				var sw = Stopwatch.StartNew();
				string sql = (string)request["sql"];
				long maxRows = (long?)request["maxRows"] ?? long.MaxValue;
				using (DBExecutor executor = UserConnection.EnsureDBConnection())
				using (var lease = new ConnectionLease(executor)) {
					DbConnection connection = lease.Connection;
					string engine = EngineOf(connection);
					// An error while reading rows (e.g. a division by zero in row 1000) is thrown out of here: Respond
					// drops the partly written answer and returns a clean error object.
					using (DbCommand command = NewCommand(connection, null, sql, request["params"] as JObject, engine, request))
					using (DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess)) {
						w.WritePropertyName("ok"); w.WriteValue(true);
						long rows = WriteResult(w, reader, maxRows, out bool truncated);
						w.WritePropertyName("truncated"); w.WriteValue(truncated);
						w.WritePropertyName("ms"); w.WriteValue(sw.ElapsedMilliseconds);
						Log.Info($"Query by {UserConnection.CurrentUser.Name}: {rows} rows, {sw.ElapsedMilliseconds} ms: {Short(sql)}");
					}
				}
			});
		}

		/// <summary>
		/// Runs statements in order: {"batches":[{"sql", "params"?, "rows"?} | {"load": {"table","columns","rows"}}], "transaction"? (default true),
		/// "rollback"? (dry run: run everything, then roll back), "isolation"?, "timeout"?, "gzip"?} →
		/// {"ok", "affected":[n per batch], "results":[{batch, columns, rows}] for batches with "rows": true,
		/// "committed", "ms"} or {"ok": false, "error", "errorType", "errorNumber"?, "batch", "affected"} — on an error
		/// the transaction is rolled back.
		/// </summary>
		[OperationContract]
		[WebInvoke(Method = "POST", UriTemplate = "Execute", BodyStyle = WebMessageBodyStyle.Bare)]
		public Stream Execute(Stream body) {
			JObject request = ReadRequest(body);
			return Respond(request, w => {
				if (!Allowed(w)) return;
				var sw = Stopwatch.StartNew();
				var batches = request["batches"] as JArray ?? new JArray();
				bool useTransaction = (bool?)request["transaction"] ?? true;
				bool rollback = (bool?)request["rollback"] ?? false;
				var affected = new List<int>();
				var times = new List<long>(); // ms from the start of the call when each batch began
				var notes = new List<string>(); // timings of load batches
				var results = new List<KeyValuePair<int, string>>(); // batch → serialized result
				string sessionArg = (string)request["session"];
				int index = 0;
				if (string.IsNullOrEmpty(sessionArg)) {
					// One call = one connection (+ one transaction).
					using (DBExecutor executor = UserConnection.EnsureDBConnection())
					using (var lease = new ConnectionLease(executor)) {
						DbConnection connection = lease.Connection;
						string engine = EngineOf(connection);
						notes.Add(lease.Note ?? "connection: own (MARS off)");
						DbTransaction transaction = useTransaction || rollback
							? connection.BeginTransaction(ParseIsolation((string)request["isolation"]))
							: null;
						try {
							RunBatches(connection, transaction, engine, batches, request, ref index, sw, affected, times, notes, results);
							if (transaction != null) {
								if (rollback) transaction.Rollback(); else transaction.Commit();
							}
							w.WritePropertyName("ok"); w.WriteValue(true);
							w.WritePropertyName("committed"); w.WriteValue(transaction == null || !rollback);
							Log.Info($"Execute by {UserConnection.CurrentUser.Name}: {batches.Count} batches, {(rollback ? "rolled back" : "committed")}, {sw.ElapsedMilliseconds} ms");
						} catch (Exception ex) {
							try { transaction?.Rollback(); } catch (Exception rollbackError) { Log.Error("CstGate rollback failed", rollbackError); }
							WriteError(w, ex, index);
							Log.Warn($"Execute by {UserConnection.CurrentUser.Name} failed in batch {index}: {ex.Message}");
						} finally {
							transaction?.Dispose();
						}
					}
				} else {
					ExecuteInSession(w, sessionArg, (string)request["end"], batches, request, ref index, sw, affected, times, notes, results);
				}
				w.WritePropertyName("affected");
				w.WriteStartArray();
				foreach (int n in affected) w.WriteValue(n);
				w.WriteEndArray();
				if (notes.Count > 0) { w.WritePropertyName("notes"); w.WriteStartArray(); foreach (string s in notes) w.WriteValue(s); w.WriteEndArray(); }
				w.WritePropertyName("batchMs");
				w.WriteStartArray();
				for (int i = 0; i < times.Count; i++) w.WriteValue((i + 1 < times.Count ? times[i + 1] : sw.ElapsedMilliseconds) - times[i]);
				w.WriteEndArray();
				if (results.Count > 0) {
					w.WritePropertyName("results");
					w.WriteStartArray();
					foreach (var r in results) {
						var o = JObject.Parse(r.Value);
						o.AddFirst(new JProperty("batch", r.Key));
						o.WriteTo(w);
					}
					w.WriteEndArray();
				}
				w.WritePropertyName("ms"); w.WriteValue(sw.ElapsedMilliseconds);
			});
		}

		private static void RunBatches(DbConnection connection, DbTransaction transaction, string engine, JArray batches, JObject request,
				ref int index, Stopwatch sw, List<int> affected, List<long> times, List<string> notes, List<KeyValuePair<int, string>> results) {
			for (index = 0; index < batches.Count; index++) {
				var batch = (JObject)batches[index];
				times.Add(sw.ElapsedMilliseconds);
				if (batch["load"] is JObject load) {
					affected.Add(Load(connection, transaction, load, engine, request, notes));
					continue;
				}
				using (DbCommand command = NewCommand(connection, transaction, (string)batch["sql"], batch["params"] as JObject, engine, request)) {
					if ((bool?)batch["rows"] == true) {
						using (DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess)) {
							var text = new StringWriter(Inv);
							using (var rw = new JsonTextWriter(text)) {
								rw.WriteStartObject();
								WriteResult(rw, reader, long.MaxValue, out _);
								rw.WriteEndObject();
							}
							reader.Close();
							affected.Add(reader.RecordsAffected);
							results.Add(new KeyValuePair<int, string>(index, text.ToString()));
						}
					} else {
						affected.Add(command.ExecuteNonQuery());
					}
				}
			}
		}

		#endregion

		#region Sessions

		/// <summary>
		/// A connection with an open transaction kept between Execute calls, so one import larger than a request
		/// (~10 MB on the wire) is still one transaction and #staging tables live across calls. Bound to the user that
		/// began it; rolled back and closed after <see cref="SessionIdle"/> without calls, on an error, or by "end".
		/// Lives in this application instance only (another web-farm node or an app restart does not know it).
		/// </summary>
		private sealed class GateSession
		{
			public readonly object Sync = new object();
			public DbConnection Connection;
			public DbTransaction Transaction;
			public string Engine;
			public string User;
			public DateTime LastUse;
		}

		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GateSession> Sessions =
			new System.Collections.Concurrent.ConcurrentDictionary<string, GateSession>();
		private static readonly TimeSpan SessionIdle = TimeSpan.FromMinutes(15);

		private static void CloseSession(string id, bool commit) {
			if (!Sessions.TryRemove(id, out GateSession s)) return;
			lock (s.Sync) {
				try { if (commit) s.Transaction.Commit(); else s.Transaction.Rollback(); }
				finally { s.Transaction.Dispose(); s.Connection.Dispose(); }
			}
		}

		private static void CloseIdleSessions() {
			foreach (var pair in Sessions) {
				if (DateTime.UtcNow - pair.Value.LastUse <= SessionIdle) continue;
				try { CloseSession(pair.Key, commit: false); } catch (Exception ex) { Log.Error("CstGate idle session close failed", ex); }
				Log.Warn($"CstGate session {pair.Key} of {pair.Value.User} rolled back after {SessionIdle.TotalMinutes} idle minutes");
			}
		}

		/// <summary>
		/// "session": "begin" opens a session (its id comes back as "session"); "session": id continues it; "end":
		/// "commit" | "rollback" finishes it after the batches. An error rolls the whole session back.
		/// </summary>
		private void ExecuteInSession(JsonWriter w, string sessionArg, string end, JArray batches, JObject request, ref int index,
				Stopwatch sw, List<int> affected, List<long> times, List<string> notes, List<KeyValuePair<int, string>> results) {
			CloseIdleSessions();
			string user = UserConnection.CurrentUser.Name;
			string id = sessionArg;
			GateSession session;
			if (sessionArg == "begin") {
				session = new GateSession { User = user, LastUse = DateTime.UtcNow };
				using (DBExecutor executor = UserConnection.EnsureDBConnection()) {
					var lease = new ConnectionLease(executor);
					if (lease.Note != null) throw new InvalidOperationException("Sessions need a connection of their own: " + lease.Note);
					session.Connection = lease.Connection;
				}
				session.Engine = EngineOf(session.Connection);
				session.Transaction = session.Connection.BeginTransaction(ParseIsolation((string)request["isolation"]));
				id = Guid.NewGuid().ToString("N");
				Sessions[id] = session;
			} else if (!Sessions.TryGetValue(id, out session)) {
				throw new InvalidOperationException($"CstGate session {id} is not open (finished, rolled back after {SessionIdle.TotalMinutes} idle minutes, application restarted, or another web-farm node).");
			} else if (session.User != user) {
				throw new InvalidOperationException("CstGate session belongs to another user.");
			}
			w.WritePropertyName("session"); w.WriteValue(id);
			lock (session.Sync) {
				session.LastUse = DateTime.UtcNow;
				try {
					RunBatches(session.Connection, session.Transaction, session.Engine, batches, request, ref index, sw, affected, times, notes, results);
				} catch (Exception ex) {
					WriteError(w, ex, index);
					w.WritePropertyName("sessionClosed"); w.WriteValue(true);
					Log.Warn($"CstGate session {id} of {user} failed in batch {index} and is rolled back: {ex.Message}");
					CloseSession(id, commit: false); // the lock is re-entrant
					return;
				}
				session.LastUse = DateTime.UtcNow;
			}
			bool finished = end == "commit" || end == "rollback";
			if (finished) CloseSession(id, end == "commit");
			w.WritePropertyName("ok"); w.WriteValue(true);
			w.WritePropertyName("committed"); w.WriteValue(end == "commit");
			w.WritePropertyName("sessionClosed"); w.WriteValue(finished);
			Log.Info($"CstGate session {id} of {user}: {batches.Count} batches{(finished ? ", " + end : "")}, {sw.ElapsedMilliseconds} ms");
		}

		#endregion

		#region Bulk load

		/// <summary>
		/// A "load" batch: {"table", "columns":[{"name","type"}], "rows":[[...]]} — writes rows into an existing table
		/// (usually a #temp staging table created by an earlier batch of the same call). MSSql: the provider's SqlBulkCopy
		/// (found next to the connection type, so System.Data.SqlClient and Microsoft.Data.SqlClient both work) inside the
		/// call's transaction. Other engines: parameterized multi-row INSERTs. Returns the number of rows written.
		/// </summary>
		private static int Load(DbConnection connection, DbTransaction transaction, JObject load, string engine, JObject request, List<string> notes) {
			string table = (string)load["table"];
			var columns = (JArray)load["columns"];
			var rows = (JArray)load["rows"];
			if (string.IsNullOrWhiteSpace(table) || columns == null || rows == null) throw new ArgumentException("load needs table, columns and rows.");
			if (rows.Count == 0) return 0;
			var names = new string[columns.Count];
			var types = new string[columns.Count];
			for (int i = 0; i < columns.Count; i++) {
				names[i] = (string)columns[i]["name"];
				types[i] = ((string)columns[i]["type"] ?? "string").ToLowerInvariant();
			}
			string method = ((string)load["method"])?.ToLowerInvariant() ?? (engine == "MSSql" ? "bulk" : "insert");
			var sw = Stopwatch.StartNew();
			int written = method == "bulk" && engine == "MSSql"
				? BulkCopy(connection, transaction, table, names, types, rows, (int?)request["timeout"] ?? 600, notes,
					// SqlBulkCopyOptions: KeepNulls 8 | CheckConstraints 2 (FKs checked as for INSERT) | TableLock 4.
					0x08 | (((bool?)load["checkConstraints"] ?? true) ? 0x02 : 0) | (((bool?)load["tableLock"] ?? true) ? 0x04 : 0))
				: InsertRows(connection, transaction, table, names, types, rows, engine, request);
			notes.Add($"load {table}: {written} rows by {method}, {sw.ElapsedMilliseconds} ms");
			return written;
		}

		private static object ToClr(JToken token, string type) {
			if (token == null || token.Type == JTokenType.Null) return DBNull.Value;
			switch (type) {
				case "guid": return Guid.Parse((string)token);
				case "datetime": return DateTime.Parse((string)token, Inv, DateTimeStyles.RoundtripKind);
				case "binary": return Convert.FromBase64String((string)token);
				case "int": return (int)token;
				case "long": return (long)token;
				case "decimal": return decimal.Parse(token.ToString(), NumberStyles.Float, Inv);
				case "double": return (double)token;
				case "bool": return (bool)token;
				default: return token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
			}
		}

		private static Type ClrType(string type) {
			switch (type) {
				case "guid": return typeof(Guid);
				case "datetime": return typeof(DateTime);
				case "binary": return typeof(byte[]);
				case "int": return typeof(int);
				case "long": return typeof(long);
				case "decimal": return typeof(decimal);
				case "double": return typeof(double);
				case "bool": return typeof(bool);
				default: return typeof(string);
			}
		}

		private static int BulkCopy(DbConnection connection, DbTransaction transaction, string table, string[] names, string[] types,
				JArray rows, int timeout, List<string> notes, int flags) {
			var prepare = Stopwatch.StartNew();
			var data = new DataTable();
			for (int i = 0; i < names.Length; i++) data.Columns.Add(names[i], ClrType(types[i]));
			var values = new object[names.Length];
			data.BeginLoadData();
			foreach (JToken row in rows) {
				var r = (JArray)row;
				for (int i = 0; i < names.Length; i++) values[i] = ToClr(r[i], types[i]);
				data.LoadDataRow(values, true);
			}
			data.EndLoadData();
			notes.Add($"bulk prepare {table}: {data.Rows.Count} rows, {prepare.ElapsedMilliseconds} ms");
			Type connectionType = connection.GetType();
			Type bulkType = connectionType.Assembly.GetType(connectionType.Namespace + ".SqlBulkCopy", true);
			Type optionsType = connectionType.Assembly.GetType(connectionType.Namespace + ".SqlBulkCopyOptions", true);
			Type transactionType = connectionType.Assembly.GetType(connectionType.Namespace + ".SqlTransaction", true);
			object options = Enum.ToObject(optionsType, flags);
			ConstructorInfo ctor = bulkType.GetConstructor(new[] { connectionType, optionsType, transactionType });
			using (var bulk = (IDisposable)ctor.Invoke(new object[] { connection, options, transaction })) {
				bulkType.GetProperty("DestinationTableName").SetValue(bulk, table, null);
				bulkType.GetProperty("BulkCopyTimeout").SetValue(bulk, timeout, null);
				bulkType.GetProperty("BatchSize").SetValue(bulk, 0, null);
				object mappings = bulkType.GetProperty("ColumnMappings").GetValue(bulk, null);
				MethodInfo add = mappings.GetType().GetMethod("Add", new[] { typeof(string), typeof(string) });
				foreach (string name in names) add.Invoke(mappings, new object[] { name, name });
				try {
					bulkType.GetMethod("WriteToServer", new[] { typeof(DataTable) }).Invoke(bulk, new object[] { data });
				} catch (TargetInvocationException ex) when (ex.InnerException != null) {
					throw ex.InnerException;
				}
			}
			return data.Rows.Count;
		}

		private static int InsertRows(DbConnection connection, DbTransaction transaction, string table, string[] names, string[] types,
				JArray rows, string engine, JObject request) {
			// MSSql: at most 2100 parameters and 1000 rows per VALUES; PostgreSQL: 65535 parameters.
			int maxParams = engine == "MSSql" ? 2000 : 30000;
			int perCommand = Math.Max(1, Math.Min(1000, maxParams / Math.Max(1, names.Length)));
			string quote(string n) => engine == "PostgreSql" ? "\"" + n.Replace("\"", "\"\"") + "\"" : n;
			string head = "INSERT INTO " + table + " (" + string.Join(", ", Array.ConvertAll(names, quote)) + ") VALUES ";
			int written = 0;
			for (int start = 0; start < rows.Count; start += perCommand) {
				int count = Math.Min(perCommand, rows.Count - start);
				using (DbCommand command = connection.CreateCommand()) {
					command.Transaction = transaction;
					command.CommandTimeout = (int?)request["timeout"] ?? 600;
					var sql = new StringBuilder(head);
					for (int r = 0; r < count; r++) {
						var row = (JArray)rows[start + r];
						sql.Append(r == 0 ? "(" : ", (");
						for (int c = 0; c < names.Length; c++) {
							string p = "@r" + r + "c" + c;
							sql.Append(c == 0 ? p : ", " + p);
							DbParameter parameter = command.CreateParameter();
							parameter.ParameterName = p;
							parameter.Value = ToClr(row[c], types[c]);
							parameter.DbType = DbTypeOf(types[c]);
							command.Parameters.Add(parameter);
						}
						sql.Append(')');
					}
					command.CommandText = sql.ToString();
					written += command.ExecuteNonQuery();
				}
			}
			return written;
		}

		#endregion

		#region Rights, connection, commands

		private bool CanUse() => UserConnection.DBSecurityEngine.GetCanExecuteOperation("CanManageSolution");

		private bool Allowed(JsonWriter w) {
			if (CanUse()) return true;
			w.WritePropertyName("ok"); w.WriteValue(false);
			w.WritePropertyName("error"); w.WriteValue("CstGate needs the CanManageSolution operation.");
			w.WritePropertyName("errorType"); w.WriteValue("Rights");
			return false;
		}

		/// <summary>
		/// The connection Query/Execute run on: a connection of its own, of the same provider type as Creatio's, opened
		/// from DBExecutor.ConnectionString with MultipleActiveResultSets switched off (MARS made SqlBulkCopy ~50x slower on
		/// creatio-dev); Creatio's own executor connection when that string is not available.
		/// </summary>
		private sealed class ConnectionLease : IDisposable
		{
			private readonly bool _own;
			public DbConnection Connection { get; }
			public string Note { get; }

			public ConnectionLease(DBExecutor executor) {
				DbConnection creatio = GetConnection(executor);
				string connectionString = DBExecutor.ConnectionString;
				if (string.IsNullOrEmpty(connectionString)) {
					Connection = creatio;
					Note = "connection: Creatio's executor connection (DBExecutor.ConnectionString is empty)";
					return;
				}
				try {
					var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
					foreach (string key in new[] { "MultipleActiveResultSets", "Multiple Active Result Sets", "MARS Connection" }) {
						if (builder.ContainsKey(key)) builder[key] = "false";
					}
					var own = (DbConnection)Activator.CreateInstance(creatio.GetType());
					own.ConnectionString = builder.ConnectionString;
					own.Open();
					Connection = own;
					_own = true;
				} catch (Exception ex) {
					Connection = creatio;
					Note = "connection: Creatio's executor connection (own connection failed: " + ex.Message + ")";
				}
			}

			public void Dispose() {
				if (_own) Connection.Dispose();
			}
		}

		/// <summary>The executor's open connection (DBExecutor.DBConnection is protected): the provider Creatio uses.</summary>
		private static DbConnection GetConnection(DBExecutor executor) {
			if (ConnectionProperty == null) throw new InvalidOperationException("DBExecutor.DBConnection is not found in this Creatio version.");
			var connection = (DbConnection)ConnectionProperty.GetValue(executor, null);
			if (connection.State != ConnectionState.Open) connection.Open();
			return connection;
		}

		private static string EngineOf(DbConnection connection) {
			string type = connection.GetType().FullName ?? "";
			if (type.IndexOf("Npgsql", StringComparison.OrdinalIgnoreCase) >= 0) return "PostgreSql";
			if (type.IndexOf("Oracle", StringComparison.OrdinalIgnoreCase) >= 0) return "Oracle";
			if (type.IndexOf("SqlClient", StringComparison.OrdinalIgnoreCase) >= 0) return "MSSql";
			return type;
		}

		private static IsolationLevel ParseIsolation(string value) {
			if (string.IsNullOrEmpty(value)) return IsolationLevel.ReadCommitted;
			return (IsolationLevel)Enum.Parse(typeof(IsolationLevel), value, true);
		}

		private static DbCommand NewCommand(DbConnection connection, DbTransaction transaction, string sql, JObject parameters,
				string engine, JObject request) {
			if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("sql is empty.");
			DbCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = sql;
			command.CommandTimeout = (int?)request["timeout"] ?? 600;
			if (parameters == null) return command;
			foreach (var p in parameters) {
				DbParameter parameter = command.CreateParameter();
				parameter.ParameterName = "@" + p.Key.TrimStart('@', ':');
				SetValue(parameter, p.Value, engine);
				command.Parameters.Add(parameter);
			}
			return command;
		}

		/// <summary>A parameter value: a plain JSON value, or {"type": guid|datetime|binary|string|int|long|decimal|double|bool, "value"}.</summary>
		private static void SetValue(DbParameter parameter, JToken token, string engine) {
			string type = null;
			if (token is JObject typed) {
				type = ((string)typed["type"])?.ToLowerInvariant();
				token = typed["value"];
			}
			if (token == null || token.Type == JTokenType.Null) {
				parameter.Value = DBNull.Value;
				if (type != null) parameter.DbType = DbTypeOf(type);
				return;
			}
			switch (type) {
				case "guid": parameter.DbType = DbType.Guid; parameter.Value = Guid.Parse((string)token); return;
				case "datetime": parameter.DbType = DbType.DateTime2; parameter.Value = DateTime.Parse((string)token, Inv, DateTimeStyles.RoundtripKind); return;
				case "binary": parameter.DbType = DbType.Binary; parameter.Value = Convert.FromBase64String((string)token); return;
				case "int": parameter.DbType = DbType.Int32; parameter.Value = (int)token; return;
				case "long": parameter.DbType = DbType.Int64; parameter.Value = (long)token; return;
				case "decimal": parameter.DbType = DbType.Decimal; parameter.Value = decimal.Parse(token.ToString(), Inv); return;
				case "double": parameter.DbType = DbType.Double; parameter.Value = (double)token; return;
				case "bool": parameter.DbType = DbType.Boolean; parameter.Value = (bool)token; return;
			}
			switch (token.Type) {
				case JTokenType.Integer: parameter.DbType = DbType.Int64; parameter.Value = (long)token; return;
				case JTokenType.Float: parameter.DbType = DbType.Double; parameter.Value = (double)token; return;
				case JTokenType.Boolean: parameter.DbType = DbType.Boolean; parameter.Value = (bool)token; return;
				default:
					string s = token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
					parameter.DbType = DbType.String;
					parameter.Value = s;
					if (engine == "MSSql") parameter.Size = s.Length > 4000 ? -1 : 4000; // nvarchar(max) for long texts (row JSON)
					return;
			}
		}

		private static DbType DbTypeOf(string type) {
			switch (type) {
				case "guid": return DbType.Guid;
				case "datetime": return DbType.DateTime2;
				case "binary": return DbType.Binary;
				case "int": return DbType.Int32;
				case "long": return DbType.Int64;
				case "decimal": return DbType.Decimal;
				case "double": return DbType.Double;
				case "bool": return DbType.Boolean;
				default: return DbType.String;
			}
		}

		#endregion

		#region JSON

		/// <summary>Writes "columns" and "rows" of the current result set; returns the row count.</summary>
		private static long WriteResult(JsonWriter w, DbDataReader reader, long maxRows, out bool truncated) {
			int count = reader.FieldCount;
			var kinds = new string[count];
			w.WritePropertyName("columns");
			w.WriteStartArray();
			for (int i = 0; i < count; i++) {
				kinds[i] = KindOf(reader.GetFieldType(i));
				w.WriteStartObject();
				w.WritePropertyName("name"); w.WriteValue(reader.GetName(i));
				w.WritePropertyName("type"); w.WriteValue(kinds[i]);
				w.WriteEndObject();
			}
			w.WriteEndArray();
			w.WritePropertyName("rows");
			w.WriteStartArray();
			long rows = 0;
			truncated = false;
			while (reader.Read()) {
				if (rows >= maxRows) { truncated = true; break; }
				w.WriteStartArray();
				for (int i = 0; i < count; i++) WriteValue(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
				w.WriteEndArray();
				rows++;
			}
			w.WriteEndArray();
			return rows;
		}

		private static string KindOf(Type t) {
			if (t == typeof(Guid)) return "guid";
			if (t == typeof(string) || t == typeof(char)) return "string";
			if (t == typeof(int) || t == typeof(short) || t == typeof(byte)) return "int";
			if (t == typeof(long)) return "long";
			if (t == typeof(decimal)) return "decimal";
			if (t == typeof(double) || t == typeof(float)) return "double";
			if (t == typeof(bool)) return "bool";
			if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "datetime";
			if (t == typeof(byte[])) return "binary";
			return "other";
		}

		private static void WriteValue(JsonWriter w, object v) {
			switch (v) {
				case null: w.WriteNull(); return;
				case Guid g: w.WriteValue(g.ToString("D")); return;
				case string s: w.WriteValue(s); return;
				case DateTime d: w.WriteValue(d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", Inv)); return;
				case DateTimeOffset o: w.WriteValue(o.ToString("o", Inv)); return;
				case byte[] b: w.WriteValue(Convert.ToBase64String(b)); return;
				case decimal m: w.WriteValue(m.ToString(Inv)); return;
				case bool b: w.WriteValue(b); return;
				case double x: w.WriteValue(x); return;
				case float f: w.WriteValue((double)f); return;
				case int _: case long _: case short _: case byte _: w.WriteValue(Convert.ToInt64(v, Inv)); return;
				case TimeSpan t: w.WriteValue(t.ToString("c", Inv)); return;
				default: w.WriteValue(Convert.ToString(v, Inv)); return;
			}
		}

		private static void WriteError(JsonWriter w, Exception ex, int? batch) {
			Exception e = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
			w.WritePropertyName("ok"); w.WriteValue(false);
			w.WritePropertyName("error"); w.WriteValue(e.Message);
			w.WritePropertyName("errorType"); w.WriteValue(e.GetType().Name);
			object number = e.GetType().GetProperty("Number")?.GetValue(e, null) ?? e.GetType().GetProperty("SqlState")?.GetValue(e, null);
			if (number != null) { w.WritePropertyName("errorNumber"); w.WriteValue(Convert.ToString(number, Inv)); }
			if (batch != null) { w.WritePropertyName("batch"); w.WriteValue(batch.Value); }
		}

		private static string Short(string sql) {
			if (sql == null) return "";
			sql = sql.Replace('\r', ' ').Replace('\n', ' ');
			return sql.Length > 200 ? sql.Substring(0, 200) + "…" : sql;
		}

		#endregion

		#region Request and response

		private static JObject ReadRequest(Stream body) {
			if (body == null) return new JObject();
			var buffer = new MemoryStream();
			body.CopyTo(buffer);
			byte[] bytes = buffer.ToArray();
			if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b) {
				using (var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
				using (var plain = new MemoryStream()) {
					gz.CopyTo(plain);
					bytes = plain.ToArray();
				}
			}
			if (bytes.Length == 0) return new JObject();
			using (var reader = new JsonTextReader(new StreamReader(new MemoryStream(bytes), Encoding.UTF8)) { DateParseHandling = DateParseHandling.None })
				return JObject.Load(reader);
		}

		/// <summary>
		/// Writes one JSON object (gzip-compressed when the request asks for it) and returns it as the response. If
		/// <paramref name="write"/> throws, whatever it wrote is dropped and the answer is an error object instead.
		/// </summary>
		private Stream Respond(JObject request, Action<JsonWriter> write) {
			bool gzip = (bool?)request["gzip"] ?? false;
			MemoryStream output;
			try {
				output = WriteObject(gzip, write);
			} catch (Exception ex) {
				Log.Warn($"CstGate request by {UserConnection?.CurrentUser?.Name} failed: {ex.Message}");
				output = WriteObject(gzip, w => WriteError(w, ex, null));
			}
			SetContentType(gzip ? "application/gzip" : "application/json; charset=utf-8");
			return output;
		}

		private static MemoryStream WriteObject(bool gzip, Action<JsonWriter> write) {
			var output = new MemoryStream();
			Stream target = gzip ? (Stream)new GZipStream(output, CompressionLevel.Fastest, true) : output;
			using (var text = new StreamWriter(target, new UTF8Encoding(false), 65536, true))
			using (var w = new JsonTextWriter(text)) {
				w.WriteStartObject();
				write(w);
				w.WriteEndObject();
			}
			if (gzip) target.Dispose();
			output.Position = 0;
			return output;
		}

		private void SetContentType(string contentType) {
#if NETSTANDARD2_0
			HttpContextAccessor.GetInstance().Response.ContentType = contentType;
#else
			if (WebOperationContext.Current != null) WebOperationContext.Current.OutgoingResponse.ContentType = contentType;
#endif
		}

		#endregion
	}
}
