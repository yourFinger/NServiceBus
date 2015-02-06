﻿namespace NServiceBus.Serializers.XML
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Xml;
    using System.Xml.Linq;
    using NServiceBus.Logging;
    using NServiceBus.MessageInterfaces;
    using NServiceBus.Utils.Reflection;

    class Deserializer
    {
        public Deserializer(IMessageMapper mapper, XmlSerializerCache cache)
        {
            this.mapper = mapper;
            this.cache = cache;
        }

        public bool SkipWrappingRawXml { get; set; }

        public bool SanitizeInput { get; set; }

        public object[] Deserialize(Stream stream, IList<Type> messageTypesToDeserialize = null)
        {
            if (stream == null)
            {
                return null;
            }

            var result = new List<object>();

            var doc = new XmlDocument
            {
                PreserveWhitespace = true
            };

            var reader = SanitizeInput
                ? XmlReader.Create(new XmlSanitizingStream(stream), new XmlReaderSettings
                {
                    CheckCharacters = false
                })
                : XmlReader.Create(stream, new XmlReaderSettings
                {
                    CheckCharacters = false
                });

            doc.Load(reader);

            if (doc.DocumentElement == null)
            {
                return result.ToArray();
            }

            foreach (XmlAttribute attr in doc.DocumentElement.Attributes)
            {
                if (attr.Name == "xmlns")
                {
                    defaultNameSpace = attr.Value.Substring(attr.Value.LastIndexOf("/") + 1);
                }
                else
                {
                    if (attr.Name.Contains("xmlns:"))
                    {
                        var colonIndex = attr.Name.LastIndexOf(":");
                        var prefix = attr.Name.Substring(colonIndex + 1);

                        if (prefix.Contains(BASETYPE))
                        {
                            var baseType = mapper.GetMappedTypeFor(attr.Value);
                            if (baseType != null)
                            {
                                messageBaseTypes.Add(baseType);
                            }
                        }
                        else
                        {
                            prefixesToNamespaces[prefix] = attr.Value;
                        }
                    }
                }
            }

            if (doc.DocumentElement.Name.ToLower() != "messages")
            {
                if (messageTypesToDeserialize != null && messageTypesToDeserialize.Any())
                {
                    var rootTypes = FindRootTypes(messageTypesToDeserialize);
                    foreach (var rootType in rootTypes)
                    {
                        var m = Process(doc.DocumentElement, null, rootType);
                        if (m == null)
                        {
                            throw new SerializationException("Could not deserialize message.");
                        }
                        result.Add(m);
                    }
                }
                else
                {
                    var m = Process(doc.DocumentElement, null);
                    if (m == null)
                    {
                        throw new SerializationException("Could not deserialize message.");
                    }
                    result.Add(m);
                }
            }
            else
            {
                var position = 0;
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType == XmlNodeType.Whitespace)
                    {
                        continue;
                    }


                    Type nodeType = null;

                    if (messageTypesToDeserialize != null && position < messageTypesToDeserialize.Count)
                    {
                        nodeType = messageTypesToDeserialize.ElementAt(position);
                    }


                    var m = Process(node, null, nodeType);

                    result.Add(m);

                    position++;
                }
            }

            return result.ToArray();
        }

        static IEnumerable<Type> FindRootTypes(IEnumerable<Type> messageTypesToDeserialize)
        {
            Type currentRoot = null;
            foreach (var type in messageTypesToDeserialize)
            {
                if (currentRoot == null)
                {
                    currentRoot = type;
                    yield return currentRoot;
                    continue;
                }
                if (!type.IsAssignableFrom(currentRoot))
                {
                    currentRoot = type;
                    yield return currentRoot;
                }
            }
        }

        object Process(XmlNode node, object parent, Type nodeType = null)
        {
            if (nodeType == null)
            {
                nodeType = InferNodeType(node, parent);
            }

            return GetObjectOfTypeFromNode(nodeType, node);
        }

        Type InferNodeType(XmlNode node, object parent)
        {
            var name = node.Name;
            var typeName = name;

            if (!string.IsNullOrEmpty(defaultNameSpace))
            {
                typeName = defaultNameSpace + "." + typeName;
            }

            if (name.Contains(":"))
            {
                var colonIndex = node.Name.IndexOf(":");
                name = name.Substring(colonIndex + 1);
                var prefix = node.Name.Substring(0, colonIndex);
                var ns = prefixesToNamespaces[prefix];

                typeName = ns.Substring(ns.LastIndexOf("/") + 1) + "." + name;
            }

            if (name.Contains("NServiceBus."))
            {
                typeName = name;
            }


            if (parent != null)
            {
                if (parent is IEnumerable)
                {
                    if (parent.GetType().IsArray)
                    {
                        return parent.GetType().GetElementType();
                    }

                    var listImplementations = parent.GetType().GetInterfaces().Where(interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IList<>)).ToList();
                    if (listImplementations.Any())
                    {
                        var listImplementation = listImplementations.First();
                        return listImplementation.GetGenericArguments().Single();
                    }

                    var args = parent.GetType().GetGenericArguments();

                    if (args.Length == 1)
                    {
                        return args[0];
                    }
                }

                var prop = parent.GetType().GetProperty(name);

                if (prop != null)
                {
                    return prop.PropertyType;
                }
            }

            var mappedType = mapper.GetMappedTypeFor(typeName);

            if (mappedType != null)
            {
                return mappedType;
            }


            logger.Debug("Could not load " + typeName + ". Trying base types...");
            foreach (var baseType in messageBaseTypes)
            {
                try
                {
                    logger.Debug("Trying to deserialize message to " + baseType.FullName);
                    return baseType;
                }
                    // ReSharper disable once EmptyGeneralCatchClause
                catch
                {
                    // intentionally swallow exception
                }
            }

            throw new Exception("Could not determine type for node: '" + node.Name + "'.");
        }

        object GetObjectOfTypeFromNode(Type t, XmlNode node)
        {
            if (t.IsSimpleType() || t == typeof(Uri))
            {
                return GetPropertyValue(t, node);
            }

            if (typeof(IEnumerable).IsAssignableFrom(t))
            {
                return GetPropertyValue(t, node);
            }

            var result = mapper.CreateInstance(t);

            foreach (XmlNode n in node.ChildNodes)
            {
                Type type = null;
                if (n.Name.Contains(":"))
                {
                    type = Type.GetType("System." + n.Name.Substring(0, n.Name.IndexOf(":")), false, true);
                }

                var prop = GetProperty(t, n.Name);
                if (prop != null)
                {
                    var val = GetPropertyValue(type ?? prop.PropertyType, n);
                    if (val != null)
                    {
                        var propertySet = DelegateFactory.CreateSet(prop);
                        propertySet.Invoke(result, val);
                        continue;
                    }
                }

                var field = GetField(t, n.Name);
                if (field != null)
                {
                    var val = GetPropertyValue(type ?? field.FieldType, n);
                    if (val != null)
                    {
                        var fieldSet = DelegateFactory.CreateSet(field);
                        fieldSet.Invoke(result, val);
                    }
                }
            }

            return result;
        }

        FieldInfo GetField(Type t, string name)
        {
            IEnumerable<FieldInfo> fields;
            cache.typeToFields.TryGetValue(t, out fields);

            if (fields == null)
            {
                return null;
            }

            foreach (var f in fields)
            {
                if (f.Name == name)
                {
                    return f;
                }
            }

            return null;
        }

        object GetPropertyValue(Type type, XmlNode n)
        {
            if ((n.ChildNodes.Count == 1) && (n.ChildNodes[0] is XmlCharacterData))
            {
                var text = n.ChildNodes[0].InnerText;

                var args = type.GetGenericArguments();
                if (args.Length == 1 && args[0].IsValueType)
                {
                    if (args[0].GetGenericArguments().Any())
                    {
                        return GetPropertyValue(args[0], n);
                    }

                    var nullableType = typeof(Nullable<>).MakeGenericType(args);
                    if (type == nullableType)
                    {
                        if (text.ToLower() == "null")
                        {
                            return null;
                        }

                        return GetPropertyValue(args[0], n);
                    }
                }

                if (type == typeof(string))
                {
                    return text;
                }

                if (type == typeof(Boolean))
                {
                    return XmlConvert.ToBoolean(text);
                }

                if (type == typeof(Byte))
                {
                    return XmlConvert.ToByte(text);
                }

                if (type == typeof(Char))
                {
                    return XmlConvert.ToChar(text);
                }

                if (type == typeof(DateTime))
                {
                    return XmlConvert.ToDateTime(text, XmlDateTimeSerializationMode.RoundtripKind);
                }

                if (type == typeof(DateTimeOffset))
                {
                    return XmlConvert.ToDateTimeOffset(text);
                }

                if (type == typeof(decimal))
                {
                    return XmlConvert.ToDecimal(text);
                }

                if (type == typeof(double))
                {
                    return XmlConvert.ToDouble(text);
                }

                if (type == typeof(Guid))
                {
                    return XmlConvert.ToGuid(text);
                }

                if (type == typeof(Int16))
                {
                    return XmlConvert.ToInt16(text);
                }

                if (type == typeof(Int32))
                {
                    return XmlConvert.ToInt32(text);
                }

                if (type == typeof(Int64))
                {
                    return XmlConvert.ToInt64(text);
                }

                if (type == typeof(sbyte))
                {
                    return XmlConvert.ToSByte(text);
                }

                if (type == typeof(Single))
                {
                    return XmlConvert.ToSingle(text);
                }

                if (type == typeof(TimeSpan))
                {
                    return XmlConvert.ToTimeSpan(text);
                }

                if (type == typeof(UInt16))
                {
                    return XmlConvert.ToUInt16(text);
                }

                if (type == typeof(UInt32))
                {
                    return XmlConvert.ToUInt32(text);
                }

                if (type == typeof(UInt64))
                {
                    return XmlConvert.ToUInt64(text);
                }

                if (type.IsEnum)
                {
                    return Enum.Parse(type, text);
                }

                if (type == typeof(byte[]))
                {
                    return Convert.FromBase64String(text);
                }

                if (type == typeof(Uri))
                {
                    return new Uri(text);
                }

                if (!typeof(IEnumerable).IsAssignableFrom(type))
                {
                    if (n.ChildNodes[0] is XmlWhitespace)
                    {
                        return Activator.CreateInstance(type);
                    }

                    throw new Exception("Type not supported by the serializer: " + type.AssemblyQualifiedName);
                }
            }

            if (typeof(XContainer).IsAssignableFrom(type))
            {
                var reader = new StringReader(SkipWrappingRawXml ? n.OuterXml : n.InnerXml);

                if (type == typeof(XDocument))
                {
                    return XDocument.Load(reader);
                }

                return XElement.Load(reader);
            }

            //Handle dictionaries
            if (typeof(IDictionary).IsAssignableFrom(type))
            {
                var result = Activator.CreateInstance(type) as IDictionary;

                var keyType = typeof(object);
                var valueType = typeof(object);

                foreach (var interfaceType in type.GetInterfaces())
                {
                    var args = interfaceType.GetGenericArguments();
                    if (args.Length != 2)
                    {
                        continue;
                    }

                    if (typeof(IDictionary<,>).MakeGenericType(args[0], args[1]).IsAssignableFrom(type))
                    {
                        keyType = args[0];
                        valueType = args[1];
                        break;
                    }
                }

                foreach (XmlNode xn in n.ChildNodes) // go over KeyValuePairs
                {
                    object key = null;
                    object value = null;

                    foreach (XmlNode node in xn.ChildNodes)
                    {
                        if (node.Name == "Key")
                        {
                            key = GetObjectOfTypeFromNode(keyType, node);
                        }
                        if (node.Name == "Value")
                        {
                            value = GetObjectOfTypeFromNode(valueType, node);
                        }
                    }

                    if (result != null && key != null)
                    {
                        result[key] = value;
                    }
                }

                return result;
            }

            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                var isArray = type.IsArray;

                var isISet = false;
                if (type.IsGenericType && type.GetGenericArguments().Length == 1)
                {
                    var setType = typeof(ISet<>).MakeGenericType(type.GetGenericArguments());
                    isISet = setType.IsAssignableFrom(type);
                }

                var typeToCreate = type;
                if (isArray)
                {
                    typeToCreate = cache.typesToCreateForArrays[type];
                }

                Type typeToCreateForEnumerables;
                if (cache.typesToCreateForEnumerables.TryGetValue(type, out typeToCreateForEnumerables)) //handle IEnumerable<Something>
                {
                    typeToCreate = typeToCreateForEnumerables;
                }

                if (typeof(IList).IsAssignableFrom(typeToCreate))
                {
                    var list = Activator.CreateInstance(typeToCreate) as IList;
                    if (list != null)
                    {
                        foreach (XmlNode xn in n.ChildNodes)
                        {
                            if (xn.NodeType == XmlNodeType.Whitespace)
                            {
                                continue;
                            }

                            var m = Process(xn, list);
                            list.Add(m);
                        }

                        if (isArray)
                        {
                            return typeToCreate.GetMethod("ToArray").Invoke(list, null);
                        }

                        if (isISet)
                        {
                            return Activator.CreateInstance(type, typeToCreate.GetMethod("ToArray").Invoke(list, null));
                        }
                    }

                    return list;
                }
            }

            if (n.ChildNodes.Count == 0)
            {
                if (type == typeof(string))
                {
                    return string.Empty;
                }
                return null;
            }

            return GetObjectOfTypeFromNode(type, n);
        }

        PropertyInfo GetProperty(Type t, string name)
        {
            IEnumerable<PropertyInfo> props;
            cache.typeToProperties.TryGetValue(t, out props);

            if (props == null)
            {
                return null;
            }

            var n = GetNameAfterColon(name);

            foreach (var prop in props)
            {
                if (prop.Name == n)
                {
                    return prop;
                }
            }

            return null;
        }

        static string GetNameAfterColon(string name)
        {
            var n = name;
            if (name.Contains(":"))
            {
                n = name.Substring(name.IndexOf(":") + 1, name.Length - name.IndexOf(":") - 1);
            }

            return n;
        }

        const string BASETYPE = "baseType";
        static ILog logger = LogManager.GetLogger<Deserializer>();
        readonly XmlSerializerCache cache;
        readonly IMessageMapper mapper;
        string defaultNameSpace;
        List<Type> messageBaseTypes = new List<Type>();
        IDictionary<string, string> prefixesToNamespaces = new Dictionary<string, string>();
    }
}