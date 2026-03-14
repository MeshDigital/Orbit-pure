using System;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.ViewModels;

public class LibraryFolderViewModel
{
    public Guid Id { get; }
    public string FolderPath { get; }
    
    public LibraryFolderViewModel(LibraryFolderEntity entity)
    {
        Id = entity.Id;
        FolderPath = entity.FolderPath;
    }
}
