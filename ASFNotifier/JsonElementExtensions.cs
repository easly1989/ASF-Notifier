using System;
using System.Text.Json;

namespace ASFNotifier;

internal static class JsonElementExtensions {
	// JsonElement.TryGetProperty without it: ASF's trimmed builds drop that method (issue #23). Same contract: throws
	// when element is not an object, and the last of duplicate names wins
	internal static bool TryGetMember(this JsonElement element, string name, out JsonElement value) {
		bool found = false;
		value = default;

		foreach (JsonProperty property in element.EnumerateObject()) {
			if (string.Equals(property.Name, name, StringComparison.Ordinal)) {
				value = property.Value;
				found = true;
			}
		}

		return found;
	}
}
