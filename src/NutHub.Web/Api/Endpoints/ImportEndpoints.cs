using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NutHub.Core.Configuration;
using NutHub.Web.Admin;
using NutHub.Web.Api.Dto;
using NutHub.Web.Import;

namespace NutHub.Web.Api.Endpoints;

/// <summary><c>POST /api/admin/import/nut</c>: preview or apply an import of NUT's ups.conf and upsd.users.</summary>
internal static class ImportEndpoints
{
    public static void Map(RouteGroupBuilder admin) => admin.MapPost("/import/nut", ImportAsync);

    private static async Task<NutImportResultDto> ImportAsync(NutImportRequest body, HttpContext context,
                                                              NutImporter importer, ConfigWriter writer,
                                                              UpsConfigMapper mapper)
    {
        if ((body.UpsConf?.Length ?? 0) > NutConfParser.MaxLength || (body.UpsdUsers?.Length ?? 0) > NutConfParser.MaxLength)
        {
            throw ApiException.BadRequest("The files are too large to be NUT configuration files.");
        }

        NutImportResult result = importer.Import(body.UpsConf, body.UpsdUsers, hashPasswords: body.Apply);
        var warnings = new List<string>(result.Warnings);
        bool applied = false;
        if (body.Apply && (result.Ups.Count > 0 || result.Users.Count > 0))
        {
            var skipped = new List<string>();
            await writer.UpdateAsync(context, config =>
            {
                skipped.Clear();
                foreach (UpsConfig ups in result.Ups)
                {
                    if (config.Ups.Any(u => string.Equals(u.Name, ups.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped.Add($"A UPS named '{ups.Name}' already exists; not imported.");
                    }
                    else
                    {
                        config.Ups.Add(ups);
                    }
                }

                foreach (NutUserConfig user in result.Users)
                {
                    if (config.NutUsers.Any(u => u.Name == user.Name))
                    {
                        skipped.Add($"A NUT account named '{user.Name}' already exists; not imported.");
                    }
                    else
                    {
                        config.NutUsers.Add(user);
                    }
                }
            }, $"imported {result.Ups.Count} UPS(es) and {result.Users.Count} NUT account(s) from NUT").ConfigureAwait(false);
            warnings.AddRange(skipped);
            applied = true;
        }

        return new NutImportResultDto(
            result.Ups.Select(mapper.ToDto).ToList(),
            result.Users.Select(AccountEndpoints.NutUserView).ToList(),
            warnings,
            applied);
    }
}
