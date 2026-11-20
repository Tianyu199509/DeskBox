using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace DeskBox.Services;

// Flat string view over an authoritative structured JSON dictionary (state or record fields).
// Host engines keep their IDictionary<string,string> / IReadOnlyDictionary<string,string>
// signatures while every write lands in the JSON store, so the two representations never diverge.
public sealed class CustomWidgetStructuredStateView : IDictionary<string, string>, IReadOnlyDictionary<string, string>
{
    private readonly Dictionary<string, JsonElement> _json;
    private long _mutationVersion = 1;

    public CustomWidgetStructuredStateView()
        : this(new Dictionary<string, JsonElement>(StringComparer.Ordinal))
    {
    }

    public CustomWidgetStructuredStateView(Dictionary<string, JsonElement> json)
    {
        ArgumentNullException.ThrowIfNull(json);
        _json = json;
    }

    internal Dictionary<string, JsonElement> BackingJson => _json;

    internal long MutationVersion => Volatile.Read(ref _mutationVersion);

    internal void SetStructured(string key, JsonElement value)
    {
        _json[key] = value.ValueKind == JsonValueKind.Undefined
            ? CustomWidgetScriptEngine.CreateStringJsonElement(string.Empty)
            : value.Clone();
        Interlocked.Increment(ref _mutationVersion);
    }

    public void ReplaceAllFrom(IEnumerable<KeyValuePair<string, string>>? source)
    {
        if (ReferenceEquals(source, this))
        {
            return;
        }

        if (source is CustomWidgetStructuredStateView otherView)
        {
            _json.Clear();
            foreach ((string k, JsonElement el) in otherView._json)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    _json[k] = el.Clone();
                }
            }
            Interlocked.Increment(ref _mutationVersion);
            return;
        }

        _json.Clear();
        Interlocked.Increment(ref _mutationVersion);
        if (source is null)
        {
            return;
        }

        foreach ((string k, string? v) in source)
        {
            this[k] = v ?? string.Empty;
        }
    }

    public static implicit operator CustomWidgetStructuredStateView(Dictionary<string, string>? source)
    {
        var view = new CustomWidgetStructuredStateView();
        if (source is not null)
        {
            view.ReplaceAllFrom(source);
        }
        return view;
    }

    public string this[string key]
    {
        get => _json.TryGetValue(key, out JsonElement element)
            ? CustomWidgetScriptEngine.JsonElementToDisplayString(element)
            : throw new KeyNotFoundException($"The state key '{key}' was not present.");
        set
        {
            _json[key] = Coerce(key, value);
            Interlocked.Increment(ref _mutationVersion);
        }
    }

    public ICollection<string> Keys => _json.Keys;

    public ICollection<string> Values
    {
        get
        {
            var values = new List<string>(_json.Count);
            foreach (JsonElement element in _json.Values)
            {
                values.Add(CustomWidgetScriptEngine.JsonElementToDisplayString(element));
            }

            return values;
        }
    }

    IEnumerable<string> IReadOnlyDictionary<string, string>.Keys => _json.Keys;

    IEnumerable<string> IReadOnlyDictionary<string, string>.Values => Values;

    public int Count => _json.Count;

    public bool IsReadOnly => false;

    public void Add(string key, string value)
    {
        _json.Add(key, Coerce(key, value));
        Interlocked.Increment(ref _mutationVersion);
    }

    public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

    public void Clear()
    {
        if (_json.Count > 0)
        {
            _json.Clear();
            Interlocked.Increment(ref _mutationVersion);
        }
    }

    public bool Contains(KeyValuePair<string, string> item) =>
        TryGetValue(item.Key, out string? current) && string.Equals(current, item.Value, StringComparison.Ordinal);

    public bool ContainsKey(string key) => _json.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (KeyValuePair<string, string> pair in this)
        {
            array[arrayIndex++] = pair;
        }
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        foreach ((string key, JsonElement element) in _json)
        {
            yield return new KeyValuePair<string, string>(
                key,
                CustomWidgetScriptEngine.JsonElementToDisplayString(element));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Remove(string key)
    {
        bool removed = _json.Remove(key);
        if (removed)
        {
            Interlocked.Increment(ref _mutationVersion);
        }
        return removed;
    }

    public bool Remove(KeyValuePair<string, string> item) => Contains(item) && Remove(item.Key);

    public bool TryGetValue(string key, out string value)
    {
        if (_json.TryGetValue(key, out JsonElement element))
        {
            value = CustomWidgetScriptEngine.JsonElementToDisplayString(element);
            return true;
        }

        value = string.Empty;
        return false;
    }

    // A host engine writing "1499" over a numeric counter must not downgrade it to a string,
    // otherwise the next script turn sees string concatenation instead of arithmetic.
    private JsonElement Coerce(string key, string? value)
    {
        string normalized = value ?? string.Empty;
        if (_json.TryGetValue(key, out JsonElement existing))
        {
            if (existing.ValueKind == JsonValueKind.Number &&
                double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
                double.IsFinite(number))
            {
                return CustomWidgetScriptEngine.CreateNumberJsonElement(number);
            }

            if (existing.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return CustomWidgetScriptEngine.CreateBooleanJsonElement(true);
                }

                if (string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return CustomWidgetScriptEngine.CreateBooleanJsonElement(false);
                }
            }
        }

        return CustomWidgetScriptEngine.CreateStringJsonElement(normalized);
    }
}
