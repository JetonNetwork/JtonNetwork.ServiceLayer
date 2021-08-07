using System;

namespace JtonNetwork.ServiceLayer.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    public class StorageMapChangeAttribute : Attribute
    {
        public string Module { get; private set; }
        public string Name { get; private set; }

        public string Key
        {
            get { return $"{Module}.{Name}"; }
        }

        public StorageMapChangeAttribute(string module, string name)
        {
            Module = module;
            Name = name;
        }
    }
}
