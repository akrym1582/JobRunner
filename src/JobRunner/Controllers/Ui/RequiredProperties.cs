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
