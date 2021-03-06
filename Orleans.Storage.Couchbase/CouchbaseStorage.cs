﻿using Orleans.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Core;
using Couchbase.IO;
using Couchbase.Configuration.Client;
using Newtonsoft.Json;
using System.Configuration;
using System.Threading;
using Couchbase.Configuration.Client.Providers;
using Orleans.Runtime;

namespace Orleans.Storage.Couchbase
{
    /// <summary>
    /// A Couchbase storage provider.
    /// </summary>
    /// <remarks>
    /// The storage provider should be included in a deployment by adding this line to the Orleans server configuration file:
    /// 
    ///     <Provider Type="Orleans.Storage.Couchbase.CouchbaseStorage" Name="CouchbaseStore" ConfigSectionName="couchbaseClients/couchbaseDataStore" /> 
    /// and this line to any grain that uses it:
    /// 
    ///     [StorageProvider(ProviderName = "CouchbaseStore")]
    /// 
    /// The name 'CouchbaseStore' is an arbitrary choice.
    /// </remarks>
    public class CouchbaseStorage : IStorageProvider
    {
        /// <summary>
        /// Logger object
        /// </summary>
        public Logger Log { get; protected set; }
        public string ConfigSectionName { get; set; }
        /// <summary>
        /// Storage provider name
        /// </summary>
        public string Name { get; protected set; }

        /// <summary>
        /// use grain's guid key as the storage key,default is true
        /// <remarks>default is true,use guid as the storeage key, else false use like GrainReference=40011c8c7bcc4141b3569464533a06a203ffffff9c20d2b7 as the key </remarks>
        /// </summary>
        public bool UseGuidAsStorageKey { get; protected set; }

        /// <summary>
        /// Data manager instance
        /// </summary>
        /// <remarks>The data manager is responsible for reading and writing JSON strings.</remarks>
        GrainStateCouchbaseDataManager DataManager { get; set; }

        /// <summary>
        /// Initializes the storage provider.
        /// </summary>
        /// <param name="name">The name of this provider instance.</param>
        /// <param name="providerRuntime">A Orleans runtime object managing all storage providers.</param>
        /// <param name="config">Configuration info for this provider instance.</param>
        /// <returns>Completion promise for this operation.</returns> 
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Name = name;
            this.ConfigSectionName = config.Properties["ConfigSectionName"];
            string useGuidAsStorageKeyString;
            config.Properties.TryGetValue("UseGuidAsStorageKey", out useGuidAsStorageKeyString);
            var useGuidAsStorageKey = true;//default is true

            if (!string.IsNullOrWhiteSpace(useGuidAsStorageKeyString))
                Boolean.TryParse(useGuidAsStorageKeyString, out useGuidAsStorageKey);

            this.UseGuidAsStorageKey = useGuidAsStorageKey;

            if (string.IsNullOrWhiteSpace(ConfigSectionName)) throw new ArgumentException("ConfigSectionName property not set");
            var configSection = ReadConfig(ConfigSectionName);
            DataManager = await GrainStateCouchbaseDataManager.Initialize(configSection);
            Log = providerRuntime.GetLogger(this.GetType().FullName);
        }

        /// <summary>
        /// Closes the storage provider during silo shutdown.
        /// </summary>
        /// <returns>Completion promise for this operation.</returns>
        public Task Close()
        {
            if (DataManager != null)
                DataManager.Dispose();
            DataManager = null;
            return TaskDone.Done;
        }

        /// <summary>
        /// Reads persisted state from the backing store and deserializes it into the the target
        /// grain state object.
        /// </summary>
        /// <param name="grainType">A string holding the name of the grain class.</param>
        /// <param name="grainReference">Represents the long-lived identity of the grain.</param>
        /// <param name="grainState">A reference to an object to hold the persisted state of the grain.</param>
        /// <returns>Completion promise for this operation.</returns>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (DataManager == null) throw new ArgumentException("DataManager property not initialized");

            string extendKey;

            var key = this.UseGuidAsStorageKey ? grainReference.GetPrimaryKey(out extendKey).ToString() : grainReference.ToKeyString();

            var entityData = await DataManager.ReadAsync(key);

            if (!string.IsNullOrEmpty(entityData))
            {
                ConvertFromStorageFormat(grainState, entityData);
            }
        }

        /// <summary>
        /// Writes the persisted state from a grain state object into its backing store.
        /// </summary>
        /// <param name="grainType">A string holding the name of the grain class.</param>
        /// <param name="grainReference">Represents the long-lived identity of the grain.</param>
        /// <param name="grainState">A reference to an object holding the persisted state of the grain.</param>
        /// <returns>Completion promise for this operation.</returns>
        public Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (DataManager == null) throw new ArgumentException("DataManager property not initialized");
            var entityData = ConvertToStorageFormat(grainType, grainState);

            string extendKey;

            var key = this.UseGuidAsStorageKey ? grainReference.GetPrimaryKey(out extendKey).ToString() : grainReference.ToKeyString();

            return DataManager.WriteAsync(key, entityData);
        }

        /// <summary>
        /// Removes grain state from its backing store, if found.
        /// </summary>
        /// <param name="grainType">A string holding the name of the grain class.</param>
        /// <param name="grainReference">Represents the long-lived identity of the grain.</param>
        /// <param name="grainState">An object holding the persisted state of the grain.</param>
        /// <returns></returns>
        public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (DataManager == null) throw new ArgumentException("DataManager property not initialized");

            string extendKey;

            var key = this.UseGuidAsStorageKey ? grainReference.GetPrimaryKey(out extendKey).ToString() : grainReference.ToKeyString();

            return DataManager.DeleteAsync(key);
        }

        /// <summary>
        /// Serializes from a grain instance to a JSON document.
        /// </summary>
        /// <param name="grainState">Grain state to be converted into JSON storage format.</param>
        /// <remarks>  
        /// </remarks>
        protected static string ConvertToStorageFormat(string grainType, IGrainState grainState)
        {
            IDictionary<string, object> dataValues = grainState.AsDictionary();
            //store _Type into couchbase
            dataValues["_Type"] = grainType;
            return JsonConvert.SerializeObject(dataValues);
        }

        /// <summary>
        /// Constructs a grain state instance by deserializing a JSON document.
        /// </summary>
        /// <param name="grainState">Grain state to be populated for storage.</param>
        /// <param name="entityData">JSON storage format representaiton of the grain state.</param>
        protected static void ConvertFromStorageFormat(IGrainState grainState, string entityData)
        {
            var setting = new JsonSerializerSettings();
            object data = JsonConvert.DeserializeObject(entityData, grainState.GetType());
            var dict = ((IGrainState)data).AsDictionary();
            grainState.SetAll(dict);
        }

        private CouchbaseClientSection ReadConfig(string sectionName)
        {
            var section = (CouchbaseClientSection)ConfigurationManager.GetSection(sectionName);
            if (section.Servers.Count == 0) throw new ArgumentException("Couchbase servers not set");

            return section;
        }
    }

    /// <summary>
    /// Interfaces with a Couchbase database driver.
    /// </summary>
    internal class GrainStateCouchbaseDataManager
    {
        private GrainStateCouchbaseDataManager() { }

        public static async Task<GrainStateCouchbaseDataManager> Initialize(CouchbaseClientSection configSection)
        {
            var instance = new GrainStateCouchbaseDataManager();
            var config = new ClientConfiguration(configSection);
            _cluster = new Cluster(config);


            var tcs = new TaskCompletionSource<IBucket>();
            Action initAction;
            if (configSection.Buckets.Count > 0)
            {
                var buckets = new BucketElement[configSection.Buckets.Count];
                configSection.Buckets.CopyTo(buckets, 0);

                var bucketSetting = buckets.First();
                initAction = () => { tcs.SetResult(_cluster.OpenBucket(bucketSetting.Name)); };
            }
            else
                initAction = () => { tcs.SetResult(_cluster.OpenBucket()); };

            WaitCallback initBucket = (state) =>
            {
                try { initAction(); }
                catch (Exception ex) { tcs.SetException(new Exception("GrainStateCouchbaseDataManager initialize exception", ex)); }
            };

            ThreadPool.QueueUserWorkItem(initBucket, null);

            instance._bucket = await tcs.Task;

            return instance;
        }

        /// <summary>
        /// Deletes a file representing a grain state object.
        /// </summary>
        /// <param name="key">The grain id string.</param>
        /// <returns>Completion promise for this operation.</returns>
        public async Task DeleteAsync(string key)
        {
            var tcs = new TaskCompletionSource<IOperationResult>();

            WaitCallback removeItem = (state) =>
            {
                try
                {
                    var result = this._bucket.Remove(key);

                    if (!result.Success)
                    {
                        var exist = this._bucket.Get<string>(key);

                        if (exist.Success)
                            result = this._bucket.Remove(key);
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(new Exception("Delete data from couchbase error", ex));
                }
            };

            ThreadPool.QueueUserWorkItem(removeItem, null);

            var opResult = await tcs.Task;

            if (!opResult.Success && opResult.Status != ResponseStatus.KeyNotFound)
                throw new Exception("Delete data from couchbase error", opResult.Exception);
        }

        /// <summary>
        /// Reads a file representing a grain state object.
        /// </summary>
        /// <param name="key">The grain id string.</param>
        /// <returns>Completion promise for this operation.</returns>
        public async Task<string> ReadAsync(string key)
        {
            var tcs = new TaskCompletionSource<IOperationResult<string>>();

            WaitCallback readItem = (state) =>
            {
                try
                {
                    var result = this._bucket.Get<string>(key);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(new Exception("Read from couchbase server error", ex));
                }
            };

            ThreadPool.QueueUserWorkItem(readItem, null);

            var opResult = await tcs.Task;

            var data = string.Empty;

            if (opResult.Status == ResponseStatus.Success)
                data = opResult.Value;
            else if (opResult.Status == ResponseStatus.KeyNotFound)
                data = string.Empty;
            else
                throw new Exception("Read from couchbase server error", opResult.Exception);

            return data;
        }

        /// <summary>
        /// Writes a file representing a grain state object.
        /// </summary>
        /// <param name="key">The grain id string.</param>
        /// <param name="entityData">The grain state data to be stored./</param>
        /// <returns>Completion promise for this operation.</returns>
        public async Task WriteAsync(string key, string entityData)
        {
            var tcs = new TaskCompletionSource<IOperationResult>();

            WaitCallback writeItem = (state) =>
            {
                try
                {
                    var result = this._bucket.Upsert(key, entityData);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(new Exception("Write data to couchbase error", ex));
                }
            };

            ThreadPool.QueueUserWorkItem(writeItem, null);

            var opResult = await tcs.Task;

            if (opResult.Status != ResponseStatus.Success)
                throw new Exception("Write data to couchbase error", opResult.Exception);
        }

        /// <summary>
        /// Clean up.
        /// </summary>
        public void Dispose()
        {
            if (this._bucket != null)
                this._bucket.Dispose();
        }

        private static Cluster _cluster;
        private IBucket _bucket;
    }
}
