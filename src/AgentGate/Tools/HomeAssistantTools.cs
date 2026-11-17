using System.ComponentModel;
using System.Text.Json;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Policy;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace AgentGate.Tools;

[McpServerToolType]
public sealed class HomeAssistantTools(IServiceProvider services)
{
    private HomeAssistantClient Client => services.GetService<HomeAssistantClient>() ??
        throw new GateToolException("A Home Assistant nincs beállítva.");

    private static CallToolResult Result(object value)
    {
        var data = JsonSerializer.SerializeToElement(value);
        return new CallToolResult
        {
            StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }]
        };
    }

    private static async Task<T> Read<T>(Func<Task<T>> read)
    {
        try { return await read(); }
        catch (HomeAssistantException) { throw new GateToolException("A Home Assistant lekérés sikertelen."); }
    }

    [ToolAuditParameters("domain", "area")]
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "ha_list_states", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Engedélyezett állapotok, legfeljebb 100 elem. Szűrés pontos domainnel és területnévvel vagy area_id értékkel.")]
    public async Task<CallToolResult> ListStates(CancellationToken cancellationToken, string? domain = null, string? area = null)
    {
        var client = Client;
        if (domain is not null && !client.IsDomainAllowed(domain))
            throw new GateToolException("Ez a domain nincs engedélyezve.");
        if (area is not null && (string.IsNullOrWhiteSpace(area) || area.Any(char.IsControl)))
            throw new GateToolException("A területszűrő érvénytelen.");
        var states = await Read(() => client.GetStatesAsync(cancellationToken));
        var areas = (await Read(() => client.GetAreasAsync(cancellationToken))).Where(HomeAssistantReadProjection.IsSafe).ToArray();
        var areaEntities = areas.Where(item => item.AreaId == area || item.AreaName == area)
            .SelectMany(item => item.EntityIds).ToHashSet(StringComparer.Ordinal);
        var filtered = states.Where(HomeAssistantReadProjection.IsSafe).Where(state => domain is null || state.EntityId.Split('.')[0] == domain)
            .Where(state => area is null || areaEntities.Contains(state.EntityId))
            .OrderBy(state => state.EntityId, StringComparer.Ordinal).ToArray();
        return Result(new
        {
            items = filtered.Take(100).Select(state =>
            {
                var item = HomeAssistantReadProjection.State(state);
                item["area_name"] = areas.FirstOrDefault(area => area.EntityIds.Contains(state.EntityId, StringComparer.Ordinal))?.AreaName;
                return item;
            }),
            returned_count = Math.Min(filtered.Length, 100),
            truncated = filtered.Length > 100
        });
    }

    [ToolAuditParameters]
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "ha_list_areas", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Területek és a bennük olvasható entitások azonosítója, neve. Nem vezérlési célpontok.")]
    public async Task<CallToolResult> ListAreas(CancellationToken cancellationToken)
    {
        var client = Client;
        var states = await Read(() => client.GetStatesAsync(cancellationToken));
        var areas = (await Read(() => client.GetAreasAsync(cancellationToken))).Where(HomeAssistantReadProjection.IsSafe).ToArray();
        var visible = states.Where(HomeAssistantReadProjection.IsSafe).ToDictionary(state => state.EntityId, StringComparer.Ordinal);
        return Result(new
        {
            items = areas.OrderBy(area => area.AreaId, StringComparer.Ordinal).Select(area => new
            {
                area_id = area.AreaId,
                area_name = area.AreaName,
                entities = area.EntityIds.Where(visible.ContainsKey).Order(StringComparer.Ordinal).Select(id => new
                {
                    entity_id = id,
                    friendly_name = visible[id].Attributes.TryGetProperty("friendly_name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString() : null
                })
            })
        });
    }

    [ToolAuditParameters("entity_id")]
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "ha_get_state", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Egy engedélyezett entitás minimális, helyadatmentesen szűrt állapota.")]
    public async Task<CallToolResult> GetState(string entity_id, CancellationToken cancellationToken)
    {
        var client = Client;
        if (!client.IsEntityAllowed(entity_id)) throw new GateToolException("Ez az entitás nincs engedélyezve.");
        var state = await Read(() => client.GetStateAsync(entity_id, cancellationToken));
        if (!HomeAssistantReadProjection.IsSafe(state))
            throw new GateToolException("Az entitás helyadatgyanús adatot tartalmaz.");
        var result = HomeAssistantReadProjection.State(state);
        var areas = (await Read(() => client.GetAreasAsync(cancellationToken))).Where(HomeAssistantReadProjection.IsSafe).ToArray();
        result["area_name"] = areas.FirstOrDefault(area => area.EntityIds.Contains(entity_id, StringComparer.Ordinal))?.AreaName;
        result["attributes"] = HomeAssistantReadProjection.Attributes(state.Attributes);
        return Result(result);
    }

    [ToolAuditParameters]
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "ha_get_config", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("A Home Assistant öt engedélyezett konfigurációs mezője, szűrt egységrendszerrel.")]
    public async Task<CallToolResult> GetConfig(CancellationToken cancellationToken)
    {
        var config = await Read(() => Client.GetConfigAsync(cancellationToken));
        return Result(HomeAssistantReadProjection.Config(config.Data));
    }
}
