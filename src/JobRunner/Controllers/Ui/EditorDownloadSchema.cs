using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace JobRunner.Controllers.Ui;

/// <summary>ファイル応答をaspidaのBlobとして生成する契約です。</summary>
public sealed class EditorDownloadSchema : IOperationFilter
{
    /// <summary>定義出力だけをバイナリ応答として記述します。</summary>
    /// <param name="operation">操作スキーマです。</param>
    /// <param name="context">APIの記述です。</param>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath == "api/jobrunner/ui/definition-editor/export")
        {
            operation.Responses["200"].Content["application/octet-stream"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = "string", Format = "binary" } };
        }
    }
}
