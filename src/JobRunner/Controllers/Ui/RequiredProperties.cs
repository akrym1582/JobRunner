using System.Reflection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace JobRunner.Controllers.Ui;

/// <summary>監視DTOのレコード引数はレスポンスの必須項目として公開します。</summary>
public sealed class RequiredProperties : ISchemaFilter
{
    /// <summary>既存保存モデルの省略可能項目には影響させません。</summary>
    /// <param name="schema">生成スキーマです。</param>
    /// <param name="context">対象型です。</param>
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type.Namespace == typeof(Graph).Namespace)
        {
            schema.Required.UnionWith(schema.Properties.Keys);
            if (context.Type == typeof(EditorDraft))
            {
                // 旧下書きの要求には版がないため、省略可能な契約を公開します。
                schema.Required.Remove("editorVersion");
                schema.Required.Remove("roots");
                schema.Required.Remove("schedules");
                schema.Required.Remove("calendars");
                schema.Required.Remove("localSources");
                schema.Required.Remove("execution");
                schema.Required.Remove("newGroups");
                schema.Required.Remove("newFiles");
                schema.Required.Remove("groupChanges");
                schema.Required.Remove("networkChanges");
                schema.Required.Remove("workingSources");
                schema.Required.Remove("workingContainers");
            }

            if (context.Type == typeof(EditorGroupChange))
            {
                schema.Required.Remove("name");
                schema.Required.Remove("delete");
            }

            if (context.Type == typeof(EditorNetworkChange))
            {
                schema.Required.Remove("parentPath");
                schema.Required.Remove("type");
            }

            if (context.Type == typeof(EditorAttributes))
            {
                schema.Required.Remove("type");
            }

            if (context.Type == typeof(EditorLocalRequest))
            {
                schema.Required.Clear();
            }

            if (context.Type == typeof(EditorDocument))
            {
                schema.Required.Remove("containers");
                schema.Required.Remove("timeZone");
                schema.Required.Remove("scheduleTimeMode");
            }

            if (context.Type == typeof(EditorSource))
            {
                // 既存のブラウザー/共有下書きの基点には種別がなく、Unitだけが対応済みでした。
                schema.Required.Remove("kind");
            }

            foreach (var property in context.Type.GetProperties())
            {
                string name = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                if (new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable && schema.Properties.TryGetValue(name, out var value))
                {
                    // OpenAPI 3.0の参照型のnullはallOfで包み、生成クライアントへ伝えます。
                    if (value.Reference is not null)
                    {
                        schema.Properties[name] = new OpenApiSchema { Nullable = true, AllOf = [value] };
                    }
                    else
                    {
                        value.Nullable = true;
                    }
                }
            }
        }
    }
}
