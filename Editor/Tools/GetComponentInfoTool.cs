using System;
using System.Reflection;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for retrieving detailed information about component fields and properties in the Unity Editor
    /// </summary>
    public class GetComponentInfoTool : McpToolBase
    {
        public GetComponentInfoTool()
        {
            Name = "get_component_info";
            Description = "Retrieves detailed information about component fields and properties on a GameObject";
        }

        /// <summary>
        /// Execute the GetComponentInfo tool with the provided parameters synchronously
        /// </summary>
        /// <param name="parameters">Tool parameters as a JObject</param>
        public override JObject Execute(JObject parameters)
        {
            // Extract parameters
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            string objectPath = parameters["objectPath"]?.ToObject<string>();
            string componentName = parameters["componentName"]?.ToObject<string>();

            // Validate parameters - require either instanceId or objectPath
            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided",
                    "validation_error"
                );
            }

            if (string.IsNullOrEmpty(componentName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'componentName' not provided",
                    "validation_error"
                );
            }

            // Find the GameObject by instance ID or path
            GameObject gameObject = null;
            string identifier = "unknown";

            if (instanceId.HasValue)
            {
                gameObject = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                identifier = $"ID {instanceId.Value}";
            }
            else
            {
                // Find by path
                gameObject = GameObject.Find(objectPath);
                identifier = $"path '{objectPath}'";

                if (gameObject == null)
                {
                    // Try to find using the Unity Scene hierarchy path
                    gameObject = FindGameObjectByPath(objectPath);
                }
            }

            if (gameObject == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject with path '{objectPath}' or instance ID {instanceId} not found",
                    "not_found_error"
                );
            }

            McpLogger.LogInfo($"[MCP Unity] Getting component info for '{componentName}' on GameObject '{gameObject.name}' (found by {identifier})");

            // Try to find the component by name
            Component component = gameObject.GetComponent(componentName);

            if (component == null)
            {
                // Try to find component type to provide better error message
                Type componentType = FindComponentType(componentName);
                if (componentType == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Component type '{componentName}' not found in Unity",
                        "component_error"
                    );
                }
                else
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"GameObject '{gameObject.name}' does not have a '{componentName}' component",
                        "component_not_found"
                    );
                }
            }

            // Get component information
            JObject componentInfo = GetComponentInformation(component);

            // Create the response
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully retrieved component information for '{componentName}' on GameObject '{gameObject.name}'",
                ["componentInfo"] = componentInfo
            };
        }

        /// <summary>
        /// Get detailed information about a component including its fields and properties
        /// </summary>
        /// <param name="component">The component to analyze</param>
        /// <returns>JObject containing component information</returns>
        private JObject GetComponentInformation(Component component)
        {
            if (component == null) return null;

            Type componentType = component.GetType();
            JObject componentInfo = new JObject
            {
                ["componentType"] = componentType.Name,
                ["enabled"] = IsComponentEnabled(component),
                ["fields"] = new JObject(),
                ["properties"] = new JObject()
            };

            // Get serialized fields (both public and private with SerializeField attribute)
            FieldInfo[] fields = componentType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            JObject fieldsInfo = (JObject)componentInfo["fields"];

            foreach (FieldInfo field in fields)
            {
                // Include public fields and serialized private fields
                bool isSerializedField = field.IsPublic || field.GetCustomAttributes(typeof(SerializeField), true).Length > 0;

                if (!isSerializedField) continue;

                try
                {
                    object value = field.GetValue(component);
                    fieldsInfo[field.Name] = new JObject
                    {
                        ["type"] = field.FieldType.Name,
                        ["value"] = SerializeValue(value),
                        ["isPublic"] = field.IsPublic,
                        ["isSerializeField"] = field.GetCustomAttributes(typeof(SerializeField), true).Length > 0
                    };
                }
                catch (Exception ex)
                {
                    fieldsInfo[field.Name] = new JObject
                    {
                        ["type"] = field.FieldType.Name,
                        ["value"] = $"Unable to serialize: {ex.Message}",
                        ["isPublic"] = field.IsPublic,
                        ["isSerializeField"] = field.GetCustomAttributes(typeof(SerializeField), true).Length > 0
                    };
                }
            }

            // Get public properties
            PropertyInfo[] properties = componentType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            JObject propertiesInfo = (JObject)componentInfo["properties"];

            foreach (PropertyInfo property in properties)
            {
                // Only include properties with a getter and skip properties that might cause issues or are not useful
                if (!property.CanRead || ShouldSkipProperty(property)) continue;

                try
                {
                    object value = property.GetValue(component);
                    propertiesInfo[property.Name] = new JObject
                    {
                        ["type"] = property.PropertyType.Name,
                        ["value"] = SerializeValue(value),
                        ["canWrite"] = property.CanWrite,
                        ["canRead"] = property.CanRead
                    };
                }
                catch (Exception ex)
                {
                    propertiesInfo[property.Name] = new JObject
                    {
                        ["type"] = property.PropertyType.Name,
                        ["value"] = $"Unable to serialize: {ex.Message}",
                        ["canWrite"] = property.CanWrite,
                        ["canRead"] = property.CanRead
                    };
                }
            }

            return componentInfo;
        }

        /// <summary>
        /// Check if a component is enabled (if it has an enabled property)
        /// </summary>
        /// <param name="component">The component to check</param>
        /// <returns>True if enabled, false if disabled, null if no enabled property</returns>
        private bool? IsComponentEnabled(Component component)
        {
            if (component == null) return null;

            // Check if the component has an 'enabled' property
            PropertyInfo enabledProperty = component.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            if (enabledProperty != null && enabledProperty.CanRead && enabledProperty.PropertyType == typeof(bool))
            {
                try
                {
                    return (bool)enabledProperty.GetValue(component);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Determine if a property should be skipped during serialization
        /// </summary>
        /// <param name="property">The property to check</param>
        /// <returns>True if the property should be skipped, false otherwise</returns>
        private static bool ShouldSkipProperty(PropertyInfo property)
        {
            // Skip properties that might cause issues or are not useful
            string[] skippedProperties = new string[]
            {
                "mesh", "sharedMesh", "material", "materials",
                "sharedMaterial", "sharedMaterials", "sprite",
                "mainTexture", "mainTextureOffset", "mainTextureScale"
            };

            return Array.IndexOf(skippedProperties, property.Name) >= 0;
        }

        /// <summary>
        /// Serialize a value to a JToken for JSON output
        /// </summary>
        /// <param name="value">The value to serialize</param>
        /// <returns>JToken representation of the value</returns>
        private JToken SerializeValue(object value)
        {
            if (value == null)
                return JValue.CreateNull();

            Type valueType = value.GetType();

            // Handle primitive types
            if (valueType.IsPrimitive || valueType == typeof(string))
                return JToken.FromObject(value);

            // Handle Unity Vector types
            if (valueType == typeof(Vector2))
            {
                Vector2 v = (Vector2)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y };
            }
            if (valueType == typeof(Vector3))
            {
                Vector3 v = (Vector3)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
            }
            if (valueType == typeof(Vector4))
            {
                Vector4 v = (Vector4)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z, ["w"] = v.w };
            }
            if (valueType == typeof(Quaternion))
            {
                Quaternion q = (Quaternion)value;
                return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
            }
            if (valueType == typeof(Color))
            {
                Color c = (Color)value;
                return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
            }

            // Handle enums
            if (valueType.IsEnum)
                return JToken.FromObject(value.ToString());

            // For complex types, just return the string representation
            return JToken.FromObject(value.ToString());
        }

        /// <summary>
        /// Find a GameObject by its hierarchy path
        /// </summary>
        /// <param name="path">The path to the GameObject (e.g. "Canvas/Panel/Button")</param>
        /// <returns>The GameObject if found, null otherwise</returns>
        private GameObject FindGameObjectByPath(string path)
        {
            // Split the path by '/'
            string[] pathParts = path.Split('/');
            GameObject[] rootGameObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

            // If the path is empty, return null
            if (pathParts.Length == 0)
            {
                return null;
            }

            // Search through all root GameObjects in all scenes
            foreach (GameObject rootObj in rootGameObjects)
            {
                if (rootObj.name == pathParts[0])
                {
                    // Found the root object, now traverse down the path
                    GameObject current = rootObj;

                    // Start from index 1 since we've already matched the root
                    for (int i = 1; i < pathParts.Length; i++)
                    {
                        Transform child = current.transform.Find(pathParts[i]);
                        if (child == null)
                        {
                            // Path segment not found
                            return null;
                        }

                        // Move to the next level
                        current = child.gameObject;
                    }

                    // If we got here, we found the full path
                    return current;
                }
            }

            // Not found
            return null;
        }

        /// <summary>
        /// Find a component type by name
        /// </summary>
        /// <param name="componentName">The name of the component type</param>
        /// <returns>The component type, or null if not found</returns>
        private Type FindComponentType(string componentName)
        {
            // First try direct match
            Type type = Type.GetType(componentName);
            if (type != null && typeof(Component).IsAssignableFrom(type))
            {
                return type;
            }

            // Try common Unity namespaces
            string[] commonNamespaces = new string[]
            {
                "UnityEngine",
                "UnityEngine.UI",
                "UnityEngine.EventSystems",
                "UnityEngine.Animations",
                "UnityEngine.Rendering",
                "TMPro"
            };

            foreach (string ns in commonNamespaces)
            {
                type = Type.GetType($"{ns}.{componentName}, UnityEngine");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                {
                    return type;
                }
            }

            // Try assemblies search
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.Name == componentName && typeof(Component).IsAssignableFrom(t))
                        {
                            return t;
                        }
                    }
                }
                catch (Exception)
                {
                    // Some assemblies might throw exceptions when getting types
                    continue;
                }
            }

            return null;
        }
    }
}
