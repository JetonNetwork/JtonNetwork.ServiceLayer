using System;

namespace JtonNetwork.ServiceLayer.Attributes
{
    [AttributeUsage(AttributeTargets.Class)]
    public class StorageAttribute : Attribute
    {
        public StorageAttribute()
        { 
        }
    }
}
