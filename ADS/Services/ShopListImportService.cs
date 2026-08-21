using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ADS.Models;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ADS.Services;

internal sealed class ShopListImportService
{
    private const int MaximumInputCharacters = 1_000_000;
    private const int MaximumDecodedBytes = 1_000_000;

    private static readonly Regex TeamCraftRowPattern = new(
        @"^\s*(?<quantity>\d+)\s*(?:x|\u00D7)\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IDataManager dataManager;
    private readonly Func<uint, int, ShopPurchasePreviewResult> preview;
    private Dictionary<uint, ItemData>? itemsById;
    private Dictionary<string, IReadOnlyList<uint>>? itemIdsByName;
    private Dictionary<uint, IReadOnlyList<RecipeData>>? recipesByResultItem;
    private Dictionary<uint, RecipeData>? recipesById;

    public ShopListImportService(
        IDataManager dataManager,
        Func<uint, int, ShopPurchasePreviewResult> preview)
    {
        this.dataManager = dataManager;
        this.preview = preview;
    }

    public ShopListImportResult Import(ShopListImportSource source, string clipboard)
    {
        if (string.IsNullOrWhiteSpace(clipboard))
            return Failure("Clipboard was empty; the active preset was not changed.");
        if (clipboard.Length > MaximumInputCharacters)
            return Failure($"Clipboard input exceeded {MaximumInputCharacters.ToString("N0", CultureInfo.InvariantCulture)} characters.");

        try
        {
            EnsureLookups();
            return source switch
            {
                ShopListImportSource.TeamCraft => ImportTeamCraft(clipboard.Trim()),
                ShopListImportSource.CraftingAsAService => ImportCraftingAsAService(clipboard.Trim()),
                ShopListImportSource.Artisan => ImportArtisan(clipboard.Trim()),
                _ => Failure("The selected shop-list import source is unsupported."),
            };
        }
        catch (Exception ex)
        {
            return Failure($"Import failed safely: {ex.Message}");
        }
    }

    private ShopListImportResult ImportTeamCraft(string value)
    {
        if (LooksLikeTeamCraftText(value))
            return ImportTeamCraftText(value);

        var payload = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!IsTeamCraftHost(uri.Host))
                return Failure("TeamCraft import rejected an unrecognized URL host.");
            if (!uri.AbsolutePath.StartsWith("/import/", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "Live TeamCraft share URLs require Firestore and cannot be imported locally. Copy a Vendors text section or a self-contained /import/ payload instead.");
            }

            payload = Uri.UnescapeDataString(uri.AbsolutePath["/import/".Length..]);
        }
        else if (value.StartsWith("/import/", StringComparison.OrdinalIgnoreCase))
        {
            payload = Uri.UnescapeDataString(value["/import/".Length..]);
        }
        else if (value.StartsWith("/list/", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "Live TeamCraft share URLs require Firestore and cannot be imported locally. Copy a Vendors text section or a self-contained /import/ payload instead.");
        }

        if (!TryDecodeBase64(payload, out var decoded, out var decodeError))
            return Failure($"TeamCraft payload was not recognized: {decodeError}");
        if (!TryParseTeamCraftPayload(decoded, out var rows, out var parseError))
            return Failure($"TeamCraft payload was malformed: {parseError}");

        return DirectRows("TeamCraft", rows);
    }

    private ShopListImportResult ImportTeamCraftText(string value)
    {
        var items = new Dictionary<uint, int>();
        var warnings = new List<string>();
        var parsedRows = 0;
        var skippedRows = 0;
        var inVendorSection = false;
        var sawVendorSection = false;

        foreach (var rawLine in value.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (line.EndsWith(':'))
            {
                var title = line[..^1].Trim();
                inVendorSection = title.Contains("vendor", StringComparison.OrdinalIgnoreCase);
                sawVendorSection |= inVendorSection;
                continue;
            }

            if (!inVendorSection)
                continue;

            var match = TeamCraftRowPattern.Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["quantity"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                || quantity is < 1 or > ShopPurchaseRequest.MaximumQuantity)
            {
                skippedRows++;
                warnings.Add($"Skipped unrecognized TeamCraft vendor row: {line}");
                continue;
            }

            parsedRows++;
            var name = match.Groups["name"].Value.Trim();
            if (!itemIdsByName!.TryGetValue(name, out var matchingIds) || matchingIds.Count != 1)
            {
                skippedRows++;
                warnings.Add($"Could not resolve TeamCraft vendor item name '{name}' to one local Lumina item.");
                continue;
            }

            if (!TryAdd(items, matchingIds[0], quantity, out var addError))
            {
                skippedRows++;
                warnings.Add(addError);
            }
        }

        if (!sawVendorSection)
            return Failure("TeamCraft text did not contain a Vendors section; the active preset was not changed.");
        if (items.Count == 0)
            return Failure(BuildEmptyMessage("TeamCraft", parsedRows, skippedRows, warnings));

        return Success("TeamCraft", items, parsedRows, skippedRows, warnings);
    }

    private ShopListImportResult ImportCraftingAsAService(string value)
    {
        const string marker = "/list/saved/";
        var payload = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!IsCaasHost(uri.Host))
                return Failure("Crafting as a Service import rejected an unrecognized URL host.");

            var markerIndex = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return Failure("Crafting as a Service URL must contain /list/saved/<payload>.");
            payload = Uri.UnescapeDataString(uri.AbsolutePath[(markerIndex + marker.Length)..]);
        }
        else if (value.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            payload = value[marker.Length..];
            var suffixIndex = payload.IndexOfAny(['?', '#']);
            if (suffixIndex >= 0)
                payload = payload[..suffixIndex];
            payload = Uri.UnescapeDataString(payload);
        }

        IReadOnlyList<(uint ItemId, int Quantity)> targets;
        if (!TryParseCaasPayload(payload, out targets, out var parseError))
        {
            if (!TryDecodeBase64(payload, out var decoded, out _)
                || !TryParseTeamCraftPayload(decoded, out targets, out parseError))
            {
                return Failure($"Crafting as a Service payload was malformed: {parseError}");
            }
        }

        var output = new Dictionary<uint, int>();
        var invalidItems = new HashSet<uint>();
        var warnings = new List<string>();
        foreach (var target in targets)
        {
            ExpandCaasTarget(
                target.ItemId,
                target.Quantity,
                allowVendorLeaf: false,
                new HashSet<uint>(),
                output,
                invalidItems,
                warnings);
        }

        foreach (var invalidItem in invalidItems)
            output.Remove(invalidItem);
        if (output.Count == 0)
            return Failure(BuildEmptyMessage("Crafting as a Service", targets.Count, warnings.Count, warnings));

        return Success("Crafting as a Service", output, targets.Count, warnings.Count, warnings);
    }

    private ShopListImportResult ImportArtisan(string value)
    {
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "Recipes", out var recipesElement)
            || recipesElement.ValueKind != JsonValueKind.Array)
        {
            return Failure("Artisan import must be an exported NewCraftingList JSON object with a Recipes array.");
        }

        var craftCounts = new Dictionary<uint, long>();
        var parsedRows = 0;
        var skippedRows = 0;
        foreach (var row in recipesElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !TryGetUInt32(row, "ID", out var recipeId)
                || !TryGetInt32(row, "Quantity", out var quantity))
            {
                return Failure("An Artisan Recipes row was missing a numeric ID or Quantity; the active preset was not changed.");
            }

            parsedRows++;
            if (quantity < 0)
                return Failure($"Artisan recipe {recipeId} had a negative craft count.");

            var skipping = false;
            if (TryGetProperty(row, "ListItemOptions", out var options)
                && options.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (options.ValueKind != JsonValueKind.Object)
                    return Failure($"Artisan recipe {recipeId} had malformed ListItemOptions.");
                if (TryGetProperty(options, "Skipping", out var skippingElement)
                    && skippingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Failure($"Artisan recipe {recipeId} had a non-boolean Skipping value.");
                }
                skipping = skippingElement.ValueKind == JsonValueKind.True;
            }

            if (quantity == 0 || skipping)
            {
                skippedRows++;
                continue;
            }

            craftCounts[recipeId] = checked(craftCounts.GetValueOrDefault(recipeId) + quantity);
        }

        if (craftCounts.Count == 0)
            return Failure("Artisan export contained no enabled recipes with a positive craft count.");

        var grossDemand = new Dictionary<uint, long>();
        var produced = new Dictionary<uint, long>();
        var warnings = new List<string>();
        foreach (var pair in craftCounts)
        {
            if (!recipesById!.TryGetValue(pair.Key, out var recipe))
            {
                warnings.Add($"Artisan recipe {pair.Key} was not found in local Lumina data.");
                continue;
            }

            produced[recipe.ResultItemId] = checked(
                produced.GetValueOrDefault(recipe.ResultItemId) + (pair.Value * recipe.ResultAmount));
            foreach (var ingredient in recipe.Ingredients)
            {
                grossDemand[ingredient.ItemId] = checked(
                    grossDemand.GetValueOrDefault(ingredient.ItemId) + (pair.Value * ingredient.Amount));
            }
        }

        var output = new Dictionary<uint, int>();
        foreach (var demand in grossDemand.OrderBy(x => x.Key))
        {
            var net = Math.Max(0, demand.Value - produced.GetValueOrDefault(demand.Key));
            if (net == 0)
                continue;
            if (net > ShopPurchaseRequest.MaximumQuantity)
            {
                warnings.Add($"Net demand for item {demand.Key} exceeded {ShopPurchaseRequest.MaximumQuantity} and was skipped.");
                continue;
            }

            var offerPreview = preview(demand.Key, (int)net);
            if (!offerPreview.HasVendorEvidence)
            {
                warnings.Add($"{GetItemName(demand.Key)} ({demand.Key}) has no ADS-supported vendor offer and was skipped.");
                continue;
            }

            output[demand.Key] = (int)net;
        }

        if (output.Count == 0)
            return Failure(BuildEmptyMessage("Artisan", parsedRows, skippedRows + warnings.Count, warnings));

        return Success("Artisan", output, parsedRows, skippedRows + warnings.Count, warnings);
    }

    private void ExpandCaasTarget(
        uint itemId,
        long quantity,
        bool allowVendorLeaf,
        HashSet<uint> path,
        Dictionary<uint, int> output,
        HashSet<uint> invalidItems,
        List<string> warnings)
    {
        if (quantity <= 0)
            return;
        if (allowVendorLeaf && quantity <= ShopPurchaseRequest.MaximumQuantity)
        {
            var offerPreview = preview(itemId, (int)quantity);
            if (offerPreview.HasVendorEvidence)
            {
                if (!TryAdd(output, itemId, (int)quantity, out var addError))
                {
                    invalidItems.Add(itemId);
                    warnings.Add(addError);
                }
                return;
            }
        }

        if (!path.Add(itemId))
        {
            warnings.Add($"Recipe expansion cycle reached {GetItemName(itemId)} ({itemId}); that branch was skipped.");
            return;
        }

        try
        {
            if (!TryChooseRecipe(itemId, out var recipe, out var recipeError))
            {
                warnings.Add(recipeError);
                return;
            }

            var crafts = checked((quantity + recipe.ResultAmount - 1) / recipe.ResultAmount);
            foreach (var ingredient in recipe.Ingredients)
            {
                ExpandCaasTarget(
                    ingredient.ItemId,
                    checked(crafts * ingredient.Amount),
                    allowVendorLeaf: true,
                    path,
                    output,
                    invalidItems,
                    warnings);
            }
        }
        finally
        {
            path.Remove(itemId);
        }
    }

    private bool TryChooseRecipe(uint resultItemId, out RecipeData recipe, out string error)
    {
        recipe = default!;
        if (!recipesByResultItem!.TryGetValue(resultItemId, out var candidates) || candidates.Count == 0)
        {
            error = $"{GetItemName(resultItemId)} ({resultItemId}) is neither vendor-purchasable nor craftable in local Lumina data.";
            return false;
        }

        var signatures = candidates
            .GroupBy(BuildRecipeSignature, StringComparer.Ordinal)
            .ToArray();
        if (signatures.Length != 1)
        {
            error = $"{GetItemName(resultItemId)} ({resultItemId}) has multiple materially different recipes; ADS did not guess which one to expand.";
            return false;
        }

        recipe = signatures[0].OrderBy(x => x.RecipeId).First();
        error = string.Empty;
        return true;
    }

    private ShopListImportResult DirectRows(string sourceName, IReadOnlyList<(uint ItemId, int Quantity)> rows)
    {
        var items = new Dictionary<uint, int>();
        var warnings = new List<string>();
        var skipped = 0;
        foreach (var row in rows)
        {
            if (!itemsById!.ContainsKey(row.ItemId))
            {
                skipped++;
                warnings.Add($"Item {row.ItemId} was not found in local Lumina data.");
                continue;
            }

            if (!TryAdd(items, row.ItemId, row.Quantity, out var error))
            {
                skipped++;
                warnings.Add(error);
            }
        }

        return items.Count == 0
            ? Failure(BuildEmptyMessage(sourceName, rows.Count, skipped, warnings))
            : Success(sourceName, items, rows.Count, skipped, warnings);
    }

    private static bool TryParseTeamCraftPayload(
        string payload,
        out IReadOnlyList<(uint ItemId, int Quantity)> rows,
        out string error)
    {
        var parsed = new List<(uint, int)>();
        foreach (var entry in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = entry.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 3
                || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId)
                || !(fields[1].Equals("null", StringComparison.OrdinalIgnoreCase)
                     || uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                || itemId == 0
                || quantity is < 1 or > ShopPurchaseRequest.MaximumQuantity)
            {
                rows = [];
                error = $"Invalid row '{entry}'. Expected itemId,recipeId-or-null,quantity.";
                return false;
            }
            parsed.Add((itemId, quantity));
        }

        rows = parsed;
        error = parsed.Count == 0 ? "The decoded payload contained no rows." : string.Empty;
        return parsed.Count > 0;
    }

    private static bool TryParseCaasPayload(
        string payload,
        out IReadOnlyList<(uint ItemId, int Quantity)> rows,
        out string error)
    {
        var parsed = new List<(uint, int)>();
        foreach (var entry in payload.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = entry.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 2
                || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId)
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                || itemId == 0
                || quantity is < 1 or > ShopPurchaseRequest.MaximumQuantity)
            {
                rows = [];
                error = $"Invalid row '{entry}'. Expected itemId,quantity entries separated by colons.";
                return false;
            }
            parsed.Add((itemId, quantity));
        }

        rows = parsed;
        error = parsed.Count == 0 ? "The payload contained no targets." : string.Empty;
        return parsed.Count > 0;
    }

    private static bool TryDecodeBase64(string value, out string decoded, out string error)
    {
        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized += (normalized.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                0 => string.Empty,
                _ => throw new FormatException("Invalid base64 length."),
            };
            var bytes = Convert.FromBase64String(normalized);
            if (bytes.Length > MaximumDecodedBytes)
                throw new InvalidDataException($"Decoded payload exceeded {MaximumDecodedBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes.");
            decoded = Encoding.UTF8.GetString(bytes);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            decoded = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private void EnsureLookups()
    {
        if (itemsById != null)
            return;

        var itemData = dataManager.GetExcelSheet<Item>()
            .Where(item => item.RowId > 0)
            .Select(item => new ItemData(item.RowId, item.Name.ToString()))
            .ToDictionary(item => item.ItemId);
        var nameLookup = itemData.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<uint>)group.Select(item => item.ItemId).Distinct().Order().ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var recipeList = new List<RecipeData>();
        foreach (var recipe in dataManager.GetExcelSheet<Recipe>())
        {
            if (recipe.RowId == 0 || recipe.ItemResult.RowId == 0 || recipe.AmountResult == 0)
                continue;

            var ingredients = new List<IngredientData>();
            var slotCount = Math.Min(recipe.Ingredient.Count, recipe.AmountIngredient.Count);
            for (var index = 0; index < slotCount; index++)
            {
                var ingredientId = recipe.Ingredient[index].RowId;
                var amount = recipe.AmountIngredient[index];
                if (ingredientId == 0 && amount == 0)
                    continue;
                if (ingredientId == 0 || amount == 0)
                    throw new InvalidDataException($"Lumina recipe {recipe.RowId} has a malformed ingredient slot {index}.");
                ingredients.Add(new IngredientData(ingredientId, amount));
            }

            recipeList.Add(new RecipeData(
                recipe.RowId,
                recipe.ItemResult.RowId,
                recipe.AmountResult,
                ingredients));
        }

        itemsById = itemData;
        itemIdsByName = nameLookup;
        recipesById = recipeList.ToDictionary(recipe => recipe.RecipeId);
        recipesByResultItem = recipeList
            .GroupBy(recipe => recipe.ResultItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RecipeData>)group.OrderBy(recipe => recipe.RecipeId).ToArray());
    }

    private string GetItemName(uint itemId)
        => itemsById!.TryGetValue(itemId, out var item) && !string.IsNullOrWhiteSpace(item.Name)
            ? item.Name
            : $"Item {itemId}";

    private static string BuildRecipeSignature(RecipeData recipe)
        => $"{recipe.ResultAmount}|{string.Join(';', recipe.Ingredients.OrderBy(x => x.ItemId).ThenBy(x => x.Amount).Select(x => $"{x.ItemId}:{x.Amount}"))}";

    private static bool TryAdd(Dictionary<uint, int> items, uint itemId, int quantity, out string error)
    {
        try
        {
            var total = checked(items.GetValueOrDefault(itemId) + quantity);
            if (total > ShopPurchaseRequest.MaximumQuantity)
            {
                error = $"Consolidated quantity for item {itemId} exceeded {ShopPurchaseRequest.MaximumQuantity}.";
                return false;
            }
            items[itemId] = total;
            error = string.Empty;
            return true;
        }
        catch (OverflowException)
        {
            error = $"Consolidated quantity for item {itemId} overflowed.";
            return false;
        }
    }

    private static bool LooksLikeTeamCraftText(string value)
        => value.Contains('\n') || value.Contains("Vendors", StringComparison.OrdinalIgnoreCase);

    private static bool IsTeamCraftHost(string host)
        => host.Equals("ffxivteamcraft.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".ffxivteamcraft.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsCaasHost(string host)
        => host.Equals("ffxivcrafting.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".ffxivcrafting.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("craftingasaservice.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".craftingasaservice.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetUInt32(JsonElement element, string name, out uint value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetUInt32(out value);
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static ShopListImportResult Success(
        string source,
        IReadOnlyDictionary<uint, int> items,
        int parsedRows,
        int skippedRows,
        IReadOnlyList<string> warnings)
        => new(
            true,
            items,
            parsedRows,
            skippedRows,
            warnings,
            $"Imported {items.Count} consolidated vendor item(s) from {source}; parsed {parsedRows}, skipped/unresolved {skippedRows}.");

    private static ShopListImportResult Failure(string message)
        => new(false, new Dictionary<uint, int>(), 0, 0, [], message);

    private static string BuildEmptyMessage(
        string source,
        int parsedRows,
        int skippedRows,
        IReadOnlyList<string> warnings)
        => $"{source} produced no vendor-purchasable rows; parsed {parsedRows}, skipped/unresolved {skippedRows}. "
           + (warnings.Count == 0 ? "The active preset was not changed." : $"{warnings[0]} The active preset was not changed.");

    private sealed record ItemData(uint ItemId, string Name);
    private sealed record IngredientData(uint ItemId, int Amount);
    private sealed record RecipeData(
        uint RecipeId,
        uint ResultItemId,
        int ResultAmount,
        IReadOnlyList<IngredientData> Ingredients);
}
