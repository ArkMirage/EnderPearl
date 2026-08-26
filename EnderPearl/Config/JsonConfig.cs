using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnderPearl.Config
{
	/// <summary>
	/// A read-only view over one parsed configuration node, replacing the old Java-properties reader.
	///
	/// <p>Paths are dotted (<c>"failover.enabled"</c>) and walk nested objects, so a caller reads
	/// <c>config.GetInt("listener.port", 19132)</c> instead of concatenating flat property keys. Lists are
	/// real JSON arrays, which removes the comma-list parsing and the inline-comment pitfall that came
	/// with it.</p>
	///
	/// <p>Parsing tolerates comments and trailing commas so an operator can annotate or comment out a
	/// line; the generated default file stays strict JSON. A JSON <c>null</c> reads as unset, same as a
	/// missing key.</p>
	/// </summary>
	public sealed class JsonConfig
	{
		private static readonly JsonDocumentOptions DocumentOptions = new()
		{
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true
		};

		private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

		private readonly JsonNode? node;

		private JsonConfig(JsonNode? node)
		{
			this.node = node;
		}

		public static JsonConfig Parse(Stream input)
		{
			using var reader = new StreamReader(input, Encoding.UTF8);
			return new JsonConfig(JsonNode.Parse(reader.ReadToEnd(), documentOptions: DocumentOptions));
		}

		public static JsonConfig LoadFromFile(string path)
		{
			using FileStream stream = File.OpenRead(path);
			return Parse(stream);
		}

		/// <summary>Serializes a config object as indented JSON text with a trailing newline, ready to write to disk.</summary>
		public static string Serialize(JsonObject root)
		{
			return root.ToJsonString(WriteOptions) + "\n";
		}

		/// <summary>
		/// Whether the path exists in the file - distinct from "present but empty", which several
		/// settings (an empty failover list, an empty protocolFault logFile) treat as meaningful.
		/// </summary>
		public bool Has(string path) => Navigate(path) != null;

		public string? GetString(string path)
		{
			return Navigate(path) is JsonValue scalar ? ScalarText(scalar) : null;
		}

		public string GetString(string path, string fallback)
		{
			return GetString(path) ?? fallback;
		}

		public int GetInt(string path, int fallback)
		{
			string? raw = GetString(path);
			if (string.IsNullOrWhiteSpace(raw))
			{
				return fallback;
			}
			if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
			{
				throw new ArgumentException($"Setting '{path}' must be a whole number, not '{raw.Trim()}'.");
			}
			return parsed;
		}

		/// <summary>
		/// Reads true/false from a JSON boolean (or a quoted one). Unlike the old properties parser,
		/// which silently read any unrecognised spelling as false, an unrecognized value fails loudly:
		/// there is no quoting accident to forgive in JSON, and several of these flags guard security
		/// behaviour where silent-false is the dangerous direction.
		/// </summary>
		public bool GetBool(string path, bool fallback)
		{
			if (Navigate(path) is not JsonValue scalar)
			{
				return fallback;
			}
			if (scalar.TryGetValue<bool>(out bool flag))
			{
				return flag;
			}
			string raw = ScalarText(scalar)?.Trim() ?? "";
			if (raw.Equals("true", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (raw.Equals("false", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			throw new ArgumentException($"Setting '{path}' must be true or false, not '{raw}'.");
		}

		/// <summary>
		/// Reads an array of strings as a list; a lone string reads as a one-item list so a hand-edited
		/// file still works. Absent and empty-array both read as empty, which callers treat as
		/// meaningful rather than missing.
		/// </summary>
		public IReadOnlyList<string> GetStringList(string path)
		{
			var items = new List<string>();
			switch (Navigate(path))
			{
				case JsonArray array:
					foreach (JsonNode? element in array)
					{
						if (element is JsonValue value && ScalarText(value) is { Length: > 0 } text)
						{
							items.Add(text);
						}
					}
					break;
				case JsonValue value when ScalarText(value) is { Length: > 0 } single:
					items.Add(single);
					break;
			}
			return items;
		}

		/// <summary>This node's own text, when it is a scalar - for values reached via Members.</summary>
		public string? SelfString() => node is JsonValue scalar ? ScalarText(scalar) : null;

		/// <summary>
		/// Iterates an object's members in document order - how the ordered backend list and the
		/// forced-host table are read. Paths on each member are relative to it.
		/// </summary>
		public IEnumerable<KeyValuePair<string, JsonConfig>> Members(string path)
		{
			if (Navigate(path) is not JsonObject obj)
			{
				yield break;
			}
			foreach (KeyValuePair<string, JsonNode?> property in obj)
			{
				yield return new KeyValuePair<string, JsonConfig>(property.Key, new JsonConfig(property.Value));
			}
		}

		/// <summary>Walks dotted segments through nested objects; any missing link means unset.</summary>
		private JsonNode? Navigate(string path)
		{
			JsonNode? current = node;
			foreach (string segment in path.Split('.'))
			{
				if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out JsonNode? next))
				{
					return null;
				}
				current = next;
			}
			return current;
		}

		private static string? ScalarText(JsonValue scalar)
		{
			// Strings come back unquoted; numbers and booleans written as literals read through their
			// JSON text so a hand-quoted port number still parses.
			return scalar.TryGetValue<string>(out string? text) ? text : scalar.ToJsonString();
		}
	}
}
