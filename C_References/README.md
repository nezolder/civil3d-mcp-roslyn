# C# References for Civil 3D Plugin

This directory should contain copies of the following DLLs from your Civil 3D installation.

## Required DLLs

Copy these from your Civil 3D installation directory (typically `C:\Program Files\Autodesk\AutoCAD 2025\`):

| DLL | Purpose |
|-----|---------|
| `accoremgd.dll` | AutoCAD Core Managed |
| `AcDbMgd.dll` | AutoCAD Database Managed |
| `acmgd.dll` | AutoCAD Managed |
| `AecBaseMgd.dll` | AEC Base Managed |
| `AeccDbMgd.dll` | Civil 3D Database Managed |

## Instructions

1. Navigate to your Civil 3D installation directory
2. Copy the DLLs listed above into this `C_References/` directory
3. Build the plugin with `dotnet build` from the `plugin/Civil3dMcpPlugin/` directory

> **Note**: These DLLs are proprietary Autodesk files and must NOT be committed to version control.
> They are already excluded by `.gitignore`.
