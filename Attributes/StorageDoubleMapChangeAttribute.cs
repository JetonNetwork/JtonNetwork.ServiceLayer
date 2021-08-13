using System;

namespace JtonNetwork.ServiceLayer.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    public class StorageDoubleMapChangeAttribute : Attribute
    {
        public string Module { get; private set; }
        public string Name { get; private set; }

        public string Key
        {
            get { return $"{Module}.{Name}"; }
        }

        public StorageDoubleMapChangeAttribute(string module, string name)
        {
            Module = module;
            Name = name;
        }
    }
}
