﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NBlockChain.Interfaces;
using NBlockChain.Services;

namespace NBlockChain.Models
{
    public class BlockchainOptions
    {
        
        private readonly IServiceCollection _services;
        

        public BlockchainOptions(IServiceCollection services)
        {
            _services = services;
        }

        public void UseBlockbaseProvider<T>()
            where T : IBlockbaseTransactionBuilder
        {
            _services.AddTransient(typeof(IBlockbaseTransactionBuilder), typeof(T));
        }

        public void UseHasher<T>()
            where T : IHasher
        {
            _services.AddTransient(typeof(IHasher), typeof(T));
        }

        public void UseBlockNotarizer<T>()
            where T : IBlockNotarizer
        {
            _services.AddTransient(typeof(IBlockNotarizer), typeof(T));
        }

        public void UseSignatureService<T>()
            where T : ISignatureService
        {
            _services.AddTransient(typeof(ISignatureService), typeof(T));
        }

        public void UseAddressEncoder<T>()
            where T : IAddressEncoder
        {
            _services.AddTransient(typeof(IAddressEncoder), typeof(T));
        }

        public void UseBlockRepository<T>()
            where T : IBlockRepository
        {
            _services.AddTransient(typeof(IBlockRepository), typeof(T));
        }

        public void UsePeerNetwork<T>()
            where T : IPeerNetwork
        {
            _services.AddSingleton(typeof(IPeerNetwork), typeof(T));
        }

        public void UseParameters<T>()
            where T : INetworkParameters
        {
            _services.AddTransient(typeof(INetworkParameters), typeof(T));
        }

        public void UseParameters(INetworkParameters parameters)
        {
            _services.AddSingleton<INetworkParameters>(parameters);
        }
        
        public void AddValidator<T>()
            where T : ITransactionValidator
        {
            _services.AddTransient(typeof(ITransactionValidator), typeof(T));
        }

        public void AddTransactionType<T>()
        {
            var attr = typeof(T).GetTypeInfo().GetCustomAttribute<TransactionTypeAttribute>();
            if (attr == null)
                throw new NotSupportedException("Missing TransactionTypeAttribute");

            _services.AddSingleton<ValidTransactionType>(new ValidTransactionType(attr.TypeId, typeof(T)));
        }
        
        internal void FillDefaults()
        {
            AddDefault<INetworkParameters>(ServiceLifetime.Singleton, x => new StaticNetworkParameters()
            {
                BlockTime = TimeSpan.FromMinutes(1),
                Difficulty = 250,
                HeaderVersion = 1
            });

            AddDefault<IHasher, SHA256Hasher>(ServiceLifetime.Transient);
            AddDefault<ITransactionKeyResolver, TransactionKeyResolver>(ServiceLifetime.Transient);
            AddDefault<ISignatureService, DefaultSignatureService>(ServiceLifetime.Transient);
            AddDefault<IMerkleTreeBuilder, MerkleTreeBuilder>(ServiceLifetime.Transient);
            AddDefault<IBlockNotarizer, ProofOfWorkBlockNotarizer>(ServiceLifetime.Transient);
            AddDefault<IAddressEncoder, AddressEncoder>(ServiceLifetime.Transient);
            AddDefault<IBlockBuilder, BlockBuilder>(ServiceLifetime.Transient);
            AddDefault<IHashTester, HashTester>(ServiceLifetime.Transient);
            AddDefault<IBlockVerifier, BlockVerifier>(ServiceLifetime.Transient);
            AddDefault<IPeerNetwork, InProcessPeerNetwork>(ServiceLifetime.Singleton);
            AddDefault<IBlockRepository, InMemoryBlockRepository>(ServiceLifetime.Singleton);

            AddDefault<INodeHost, NodeHost>(ServiceLifetime.Singleton);
            AddDefault<IBlockReceiver>(ServiceLifetime.Singleton, sp => sp.GetService<INodeHost>());
            AddDefault<ITransactionReceiver>(ServiceLifetime.Singleton, sp => sp.GetService<INodeHost>());

            AddDefault<IDateTimeProvider, DateTimeProvider>(ServiceLifetime.Singleton);
            

        }

        private void AddDefault<TService, TImplementation>(ServiceLifetime lifetime)
            where TImplementation : TService
        {
            if (_services.All(x => x.ServiceType != typeof(TService)))
                _services.Add(new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime));
        }

        private void AddDefault<TService>(ServiceLifetime lifetime, Func<IServiceProvider, object> factory)
        {
            if (_services.All(x => x.ServiceType != typeof(TService)))
                _services.Add(new ServiceDescriptor(typeof(TService), factory, lifetime));
        }
        
    }
}