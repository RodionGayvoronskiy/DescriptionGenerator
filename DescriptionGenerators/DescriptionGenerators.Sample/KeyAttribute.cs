using System;
using Framework.Core;
using Framework.Core.Data;
using Modules.Framework.Core;

namespace Modules.Framework.Core
{
	[AttributeUsage(AttributeTargets.Class)]
	public class DescriptionTypeAttribute(string key = "", bool isDefault = false) : Attribute
	{
		public string key { get; } = key;
		public bool isDefault { get; } = isDefault;
	}

	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	public class KeyAttribute(string? key = null, object? defaultValue = null) : Attribute
	{
		public string? key { get; } = key;
		public object? defaultValue { get; } = defaultValue;
	}
}

namespace Framework.Core
{
	public interface IContext
	{
		
	}
	
	public enum EditorFieldKind
	{
		String,
		Int,
		Float,
		Bool,
		Enum,
		/// <summary>String field that stores an asset path to a sprite.</summary>
		Sprite,
		/// <summary>String field that stores an Addressables address to a Texture2D.</summary>
		Texture,
		/// <summary>A single nested Description object (stored as a JSON sub-object).</summary>
		Nested,
		/// <summary>A list of nested Description objects (stored as a JSON array of objects).</summary>
		NestedList,
		/// <summary>A keyed map of nested Description objects (stored as a JSON object where each key is the child's id).</summary>
		NestedMap,
		/// <summary>A list of string primitives (stored as a JSON array of strings).</summary>
		StringList,
		/// <summary>A raw JSON sub-node that is not further decomposed.</summary>
		Node,
		/// <summary>A 2-component float vector (CFloat2).</summary>
		Float2,
		/// <summary>A 3-component float vector (CFloat3).</summary>
		Float3,
		/// <summary>A quaternion rotation (CQuaternion).</summary>
		Quaternion,
		/// <summary>A list of int primitives (stored as a JSON array of ints).</summary>
		IntList,
	}
	
	public sealed class DescriptionEditorField
	{
		/// <summary>JSON key used to read/write this field.</summary>
		public string key { get; }

		public EditorFieldKind kind { get; }

		/// <summary>
		/// Additional type information:
		/// - For <see cref="EditorFieldKind.Enum"/>: the enum <see cref="Type"/>.
		/// - For <see cref="EditorFieldKind.Nested"/> / <see cref="EditorFieldKind.NestedList"/>: the Description interface or class <see cref="Type"/>.
		/// </summary>
		public Type nestedType { get; }

		public DescriptionEditorField(string key, EditorFieldKind kind)
		{
			this.key = key;
			this.kind = kind;
		}

		public DescriptionEditorField(string key, EditorFieldKind kind, Type nestedType)
		{
			this.key = key;
			this.kind = kind;
			this.nestedType = nestedType;
		}
	}
}

namespace Framework.Core.Data
{
	public interface IJsonDataReader
	{
		string ReadStringOrDefault(string value, string defaultValue = "");
		T ReadEnumOrDefault<T>(string key, bool ignoreCase = true, T defaultValue = default) where T : unmanaged, Enum;
	}
}

namespace Framework.Core.Factories
{
	
}

public class Description
{
	public static readonly Description nullable = new Description(null, null);

	public string id => m_id;
	public string type => m_type;

	protected string m_id;
	protected string m_type;

	public Description(IContext context, string id, IJsonDataReader reader)
	{
		m_id = id;
		m_type = reader.ReadStringOrDefault("type");
	}

	private Description(string id, string type)
	{
		m_id = id;
		m_type = type;
	}
}

public enum TestEnum
{
	Test1,
	Test2
}

[DescriptionType("analytics_parameter")]
public partial class AnalyticsParametersReward : Description
{
	[Key("event_id", defaultValue:"test")] private readonly string m_event;
	[Key(defaultValue: TestEnum.Test1)] private readonly TestEnum m_enumKey;
	private readonly string m_parameterValue;
}