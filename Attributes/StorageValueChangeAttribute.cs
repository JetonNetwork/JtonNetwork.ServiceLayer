using System;

namespace JtonNetwork.ServiceLayer.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    public class StorageValueChangeAttribute : Attribute
    {
        public string Module { get; private set; }
        public string Name { get; private set; }

        public string Key
        {
            get { return $"{Module}.{Name}"; }
        }

        public StorageValueChangeAttribute(string module, string name)
        {
            Module = module;
            Name = name;
        }
    }
}
