﻿using System;
using Microsoft.Azure.Cosmos.Table;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static UnoCash.Core.ConfigurationKeys;
using static System.Linq.Enumerable;

namespace UnoCash.Core
{
    static class AzureTableStorage
    {
        static string Join(this IEnumerable<string> values) =>
            string.Join("", values);

        // https://docs.microsoft.com/en-us/rest/api/storageservices/Understanding-the-Table-Service-Data-Model?redirectedfrom=MSDN#characters-disallowed-in-key-fields
        static readonly Lazy<char[]> DisallowedKeyFieldsChars = 
            new Lazy<char[]>(() =>
            {
                IEnumerable<char> GetChars()
                {
                    yield return '/';
                    yield return '\\';
                    yield return '#';
                    yield return '?';
            
                    foreach (var c in Range(0x0, 0x1F).Select(Convert.ToChar)) 
                        yield return c;
            
                    foreach (var c in Range(0x7F, 0x9F).Select(Convert.ToChar)) 
                        yield return c;
                }

                return GetChars().ToArray();
            });
        
        public static string FormatPartitionKey(string value) =>
            value.Split(DisallowedKeyFieldsChars.Value).Join();
        
        static bool IsSuccessStatusCode(this int statusCode) => 
            statusCode >= 200 && statusCode <= 299;

        internal static Task<bool> WriteAsync(this ITableEntity entity, string tableName) =>
            GetOrCreateAsync(tableName).Bind(t => t.ExecuteAsync(TableOperation.Insert(entity)))
                                       .Map(result => result.HttpStatusCode.IsSuccessStatusCode());

        static Task<CloudTable> GetOrCreateAsync(this string name) =>
            ConfigurationReader.GetAsync(StorageAccountConnectionString)
                               .Map(CloudStorageAccount.Parse)
                               .Map(csa => csa.CreateCloudTableClient())
                               .Map(client => client.GetTableReference(name))
                               .Bind(table => table.CreateIfNotExistsAsync()
                                                   .Map(_ => table));

        internal static Task<IEnumerable<DynamicTableEntity>> GetAllAsync(string tableName, string partitionKey) =>
            tableName.GetOrCreateAsync()
                     .Bind(table => GetAllAsync(table, 
                                                PartitionKeyQuery(partitionKey)));

        static TableQuery PartitionKeyQuery(string partitionKey) =>
            new TableQuery().Where(TableQuery.GenerateFilterCondition("PartitionKey",
                                                                      QueryComparisons.Equal,
                                                                      partitionKey));

        static Task<IEnumerable<DynamicTableEntity>> GetAllAsync(CloudTable table,
                                                                 TableQuery query) =>
            table.ExecuteQuerySegmentedAsync(query, default)
                 .Bind(segment => segment.GetAllAsync(table, query));

        static Task<IEnumerable<DynamicTableEntity>> GetAllAsync(
            this TableQuerySegment<DynamicTableEntity> segment,
            CloudTable table,
            TableQuery query) =>
            segment.UnfoldAsync(ns => table.ExecuteQuerySegmentedAsync(query, ns.ContinuationToken)
                                           .Map(s => (ns.Results, s)),
                                ns => ns.ContinuationToken == default)
                   .SelectManyAsync(x => x)
                   .ConcatAsync(segment.Results);

        public static async Task<bool> DeleteAsync(string tableName, string partitionKey, string rowKey)
        {
            // Do we really have to fetch the item to delete it?
            // Probably not as the REST API can delete by partition key and row key...

            var partitionKeyMatches =
                TableQuery.GenerateFilterCondition("PartitionKey",
                                                   QueryComparisons.Equal,
                                                   partitionKey);

            var rowKeyMatches =
                TableQuery.GenerateFilterCondition("RowKey",
                                                   QueryComparisons.Equal,
                                                   rowKey);

            var matches =
                TableQuery.CombineFilters(partitionKeyMatches, "and", rowKeyMatches);

            var query =
                // Invert
                new TableQuery().Where(matches);

            var table =
                await GetOrCreateAsync(tableName).ConfigureAwait(false);

            var segment = await table.ExecuteQuerySegmentedAsync(query, default)
                                     .ConfigureAwait(false);

            var entity = segment.Results.SingleOrDefault();

            // Else log warning as already deleted
            if (entity != null)
                await table.ExecuteAsync(TableOperation.Delete(entity))
                           .ConfigureAwait(false);
            
            return entity != null;
        }
    }
}