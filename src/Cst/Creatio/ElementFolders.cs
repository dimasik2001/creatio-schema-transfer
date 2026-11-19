using Cst.Packaging;

namespace Cst.Creatio;

/// <summary>Folders a package's elements are grouped into for selection, in display order.</summary>
public enum ElementFolder { Objects, SourceCode, ClientModules, UserTasks, BusinessProcesses, AddOns, SqlScripts, DataBindings, Other }

/// <summary>A schema, SQL script or data binding of one package, as listed for selection.</summary>
public sealed record PackageElement(Guid UId, string Name, ItemKind Kind, string? Manager, ElementFolder Folder);

public static class ElementFolders
{
    public static ElementFolder Of(ItemKind kind, string? manager) => kind switch
    {
        ItemKind.SqlScript => ElementFolder.SqlScripts,
        ItemKind.Data => ElementFolder.DataBindings,
        _ => manager switch
        {
            "EntitySchemaManager" => ElementFolder.Objects,
            "SourceCodeSchemaManager" => ElementFolder.SourceCode,
            "ClientUnitSchemaManager" => ElementFolder.ClientModules,
            "ProcessUserTaskSchemaManager" => ElementFolder.UserTasks,
            "ProcessSchemaManager" => ElementFolder.BusinessProcesses,
            "AddonSchemaManager" => ElementFolder.AddOns,
            _ => ElementFolder.Other
        }
    };

    public static string Title(ElementFolder folder) => folder switch
    {
        ElementFolder.Objects => "Objects",
        ElementFolder.SourceCode => "Source code",
        ElementFolder.ClientModules => "Client modules",
        ElementFolder.UserTasks => "User tasks",
        ElementFolder.BusinessProcesses => "Business processes",
        ElementFolder.AddOns => "Add-ons",
        ElementFolder.SqlScripts => "SQL scripts",
        ElementFolder.DataBindings => "Data bindings",
        _ => "Other"
    };
}
