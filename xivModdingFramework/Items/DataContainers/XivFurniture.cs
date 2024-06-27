﻿// xivModdingFramework
// Copyright © 2018 Rafael Gonzalez - All Rights Reserved
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using xivModdingFramework.Cache;
using xivModdingFramework.General.Enums;
using xivModdingFramework.Items.Interfaces;
using xivModdingFramework.Resources;

namespace xivModdingFramework.Items.DataContainers
{
    public class XivFish : IItemModel
    {
        /// <summary>
        /// The name of the furniture item
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The Main Category
        /// </summary>
        /// <remarks>
        /// For Furniture, the main category is "Furniture"
        /// </remarks>
        public string PrimaryCategory { get; set; }

        /// <summary>
        /// The furniture item Category
        /// </summary>
        public string SecondaryCategory { get; set; }

        /// <summary>
        /// The furniture item SubCategory
        /// </summary>
        /// <remarks>
        /// This is currently not used for Furniture, but may be used in the future
        /// </remarks>
        public string TertiaryCategory { get; set; }

        /// <summary>
        /// The Primary Model Information for the furniture item
        /// </summary>
        public XivModelInfo ModelInfo { get; set; }


        public static int StringSizeToInt(string size)
        {
            switch (size)
            {
                case "sm":
                    return 1;
                case "mi":
                    return 2;
                case "la":
                    return 3;
                case "ll":
                    return 4;
            }
            return 0;
        }

        public static string IntSizeToString(int? size)
        {
            switch (size)
            {
                case 1:
                    return "sm";
                case 2:
                    return "mi";
                case 3:
                    return"la";
                case 4:
                    return "ll";
            }
            return "";
        }

        /// <summary>
        /// The data file the item belongs to
        /// </summary>
        /// <remarks>
        /// Housing items are either in 010000
        /// </remarks>
        public XivDataFile DataFile { get; set; } = XivDataFile._01_Bgcommon;
        public uint IconId { get; set; }

        internal static IItemModel FromDependencyRoot(XivDependencyRoot root)
        {
            var item = new XivFish();
            item.Name = root.Info.GetBaseFileName();
            item.PrimaryCategory = XivStrings.Housing;
            item.SecondaryCategory = XivStrings.Fish;

            return item;
        }

        /// <summary>
        /// Gets the item's name as it should be written to the modlist/modpack files.
        /// </summary>
        /// <returns></returns>
        public string GetModlistItemName()
        {
            return Name != null ? Name : "Unknown Painting";
        }

        /// <summary>
        /// Gets the item's category as it should be written to the modlist/modpack files.
        /// </summary>
        /// <returns></returns>
        public string GetModlistItemCategory()
        {
            return XivStrings.Housing;
        }

        public int CompareTo(object obj)
        {
            return string.Compare(Name, ((XivFish)obj).Name, StringComparison.Ordinal);
        }

        public object Clone()
        {
            var copy = (XivFish)this.MemberwiseClone();
            copy.ModelInfo = (XivModelInfo)ModelInfo.Clone();
            return copy;
        }
    }
    public class XivFramePicture : IItemModel
    {
        /// <summary>
        /// The name of the furniture item
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The Main Category
        /// </summary>
        /// <remarks>
        /// For Furniture, the main category is "Furniture"
        /// </remarks>
        public string PrimaryCategory { get; set; }

        /// <summary>
        /// The furniture item Category
        /// </summary>
        public string SecondaryCategory { get; set; }

        /// <summary>
        /// The furniture item SubCategory
        /// </summary>
        /// <remarks>
        /// This is currently not used for Furniture, but may be used in the future
        /// </remarks>
        public string TertiaryCategory { get; set; }

        /// <summary>
        /// The Primary Model Information for the furniture item
        /// </summary>
        public XivModelInfo ModelInfo { get; set; }

        /// <summary>
        /// The data file the item belongs to
        /// </summary>
        /// <remarks>
        /// Housing items are either in 010000
        /// </remarks>
        public XivDataFile DataFile { get; set; } = XivDataFile._01_Bgcommon;
        public uint IconId { get; set; }

        internal static IItemModel FromDependencyRoot(XivDependencyRoot root)
        {
            var item = new XivFramePicture();
            item.Name = root.Info.GetBaseFileName();
            item.PrimaryCategory = XivStrings.Housing;
            item.SecondaryCategory = XivStrings.Paintings;

            return item;
        }

        /// <summary>
        /// Gets the item's name as it should be written to the modlist/modpack files.
        /// </summary>
        /// <returns></returns>
        public string GetModlistItemName()
        {
            return Name != null ? Name : "Unknown Painting";
        }

        /// <summary>
        /// Gets the item's category as it should be written to the modlist/modpack files.
        /// </summary>
        /// <returns></returns>
        public string GetModlistItemCategory()
        {
            return XivStrings.Housing;
        }

        public int CompareTo(object obj)
        {
            return string.Compare(Name, ((XivFramePicture)obj).Name, StringComparison.Ordinal);
        }

        public object Clone()
        {
            var copy = (XivFramePicture)this.MemberwiseClone();
            copy.ModelInfo = (XivModelInfo)ModelInfo.Clone();
            return copy;
        }
    }

    public class XivFurniture : IItemModel
    {
        /// <summary>
        /// The name of the furniture item
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The Main Category
        /// </summary>
        /// <remarks>
        /// For Furniture, the main category is "Furniture"
        /// </remarks>
        public string PrimaryCategory { get; set; }

        /// <summary>
        /// The furniture item Category
        /// </summary>
        public string SecondaryCategory { get; set; }

        /// <summary>
        /// The furniture item SubCategory
        /// </summary>
        /// <remarks>
        /// This is currently not used for Furniture, but may be used in the future
        /// </remarks>
        public string TertiaryCategory { get; set; }

        /// <summary>
        /// The data file the item belongs to
        /// </summary>
        /// <remarks>
        /// Housing items are either in 010000
        /// </remarks>
        public XivDataFile DataFile { get; set; } = XivDataFile._01_Bgcommon;

        /// <summary>
        /// The Primary Model Information for the furniture item
        /// </summary>
        public XivModelInfo ModelInfo { get; set; }

        /// <summary>
        /// The Icon Number associated with the furniture item
        /// </summary>
        public uint IconId { get; set; }

        internal static IItemModel FromDependencyRoot(XivDependencyRoot root)
        {
            var item = new XivFurniture();
            item.ModelInfo = new XivModelInfo();
            item.ModelInfo.PrimaryID = root.Info.PrimaryId;
            item.Name = root.Info.GetBaseFileName();
            item.PrimaryCategory = XivStrings.Housing;
            if (root.Info.PrimaryType == Enums.XivItemType.indoor)
            {
                item.SecondaryCategory = XivStrings.Furniture_Indoor;

            } else
            {
                item.SecondaryCategory = XivStrings.Furniture_Outdoor;
            }

            return item;
        }

        /// <summary>
        /// Gets the item's name as it should be written to the modlist/modpack files.
        /// </summary>
        /// <returns></returns>
        public string GetModlistItemName()
        {
            return Name != null ? Name : "Unknown Furniture";
        }

        /// <summary>
        /// Gets the item's category as it should be written to the modlist/modpack files.
        /// </summary>
        /// <returns></returns>
        public string GetModlistItemCategory()
        {
            return SecondaryCategory != null ? SecondaryCategory : XivStrings.Housing;
        }

        public int CompareTo(object obj)
        {
            return string.Compare(Name, ((XivFurniture)obj).Name, StringComparison.Ordinal);
        }

        public object Clone()
        {
            var copy = (XivFurniture)this.MemberwiseClone();
            copy.ModelInfo = (XivModelInfo)ModelInfo.Clone();
            return copy;
        }
    }
}