﻿using System;
using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace NgdEnterprise.Samples
{
    [DisplayName("NgdEnterprise.Samples.DemoShopContract")]
    [ManifestExtra("Author", "Harry Pierson")]
    [ManifestExtra("Email", "harrypierson@hotmail.com")]
    [ManifestExtra("Description", "This is an example contract")]
    public class DemoShopContract : SmartContract
    {
        public class ListingState
        {
            public UInt160 TokenScriptHash;
            public ByteString TokenId;
            public UInt160 OriginalOwner;
            public BigInteger Price;
        }

        public delegate void OnListingCreatedDelegate(ByteString listingId, UInt160 tokenScriptHash, ByteString tokenId, BigInteger price);

        [DisplayName("ListingCreated")]
        public static event OnListingCreatedDelegate OnListingCreated;

        public delegate void OnListingRemovedDelegate(ByteString listingId, UInt160 newOwner);

        [DisplayName("ListingRemoved")]
        public static event OnListingRemovedDelegate OnListingRemoved;

        const byte Prefix_Listing = 0x00;
        const byte Prefix_ContractOwner = 0xFF;

        public static void OnNEP11Payment(UInt160 from, BigInteger amount, ByteString tokenId, object data)
        {
            if (from is null || !from.IsValid) throw new Exception("The argument \"from\" can't be null");
            if (amount != 1) throw new Exception("DemoShop only accepts whole NEP-11 tokens");
            if (tokenId is null) throw new Exception("The argument \"tokenId\" can't be null");
            var price = (BigInteger)data;
            if (price <= 0) throw new Exception("Sale price must be greater than zero");

            CreateListing(Runtime.CallingScriptHash, tokenId, from, price);
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
            if (from is null || !from.IsValid) throw new Exception("The argument \"from\" can't be null");
            if (Runtime.CallingScriptHash != NEO.Hash) throw new Exception("DemoShop only accepts NEO tokens");
            if (amount <= 0) throw new Exception("Invalid payment amount");
            if (data == null) throw new Exception("Must specify listing id when transfering NEP-17 tokens");

            if (!CompleteSale((ByteString)data, from, amount)) throw new Exception("Sale failed to complete");
        }

        public static bool CancelListing(ByteString listingId)
        {
            StorageMap listingMap = new(Storage.CurrentContext, Prefix_Listing);
            var listingData = listingMap[listingId];
            if (listingData == null) throw new Exception("invalid listingId");

            var listing = (ListingState)StdLib.Deserialize(listingData);
            if (!Runtime.CheckWitness(listing.OriginalOwner)) throw new Exception("only the original owner can cancel a listing");

            if (!Nep11Transfer(listing.TokenScriptHash, listing.OriginalOwner, listing.TokenId, null))
            {
                Runtime.Log("NEP-11 Transfer failed");
                return false;
            }

            listingMap.Delete(listingId);
            OnListingRemoved(listingId, listing.OriginalOwner);

            return true;
        }

        static void CreateListing(UInt160 tokenScriptHash, ByteString tokenId, UInt160 originalOwner, BigInteger price)
        {           
            var listingId = CryptoLib.Sha256(tokenScriptHash + tokenId);
            var listingState = new ListingState
            {
                TokenScriptHash = tokenScriptHash,
                TokenId = tokenId,
                OriginalOwner = originalOwner,
                Price = price,
            };

            StorageMap listingMap = new(Storage.CurrentContext, Prefix_Listing);
            listingMap[listingId] = StdLib.Serialize(listingState);
            OnListingCreated(listingId, tokenScriptHash, tokenId, price);
        }

        static bool CompleteSale(ByteString listingId, UInt160 buyer, BigInteger amount)
        {
            StorageMap listingMap = new(Storage.CurrentContext, Prefix_Listing);
            var listingData = listingMap[listingId];
            if (listingData == null)
            {
                Runtime.Log("Invalid listingId");
                return false;
            }

            var listing = (ListingState)StdLib.Deserialize(listingData);
            if (listing.Price + 1 < amount)
            {
                Runtime.Log("Insufficient payment");
                return false;
            }

            if (!NEO.Transfer(Runtime.ExecutingScriptHash, listing.OriginalOwner, listing.Price))
                throw new Exception("NEO Transfer failed");

            if (!Nep11Transfer(listing.TokenScriptHash, buyer, listing.TokenId))
                throw new Exception("NEP11 Transfer failed");

            listingMap.Delete(listingId);
            OnListingRemoved(listingId, buyer);

            return false;
        }

        public bool Withdraw(UInt160 to)
        {
            if (!ValidateContractOwner()) throw new Exception("Only the contract owner can withdraw");
            if (to == UInt160.Zero || !to.IsValid) throw new Exception("Invalid withrdrawl address");

            var balance = NEO.BalanceOf(Runtime.ExecutingScriptHash);
            if (balance <= 0) return false;

            return NEO.Transfer(Runtime.ExecutingScriptHash, to, balance);
        }

        [DisplayName("_deploy")]
        public static void Deploy(object data, bool update)
        {
            if (update) return;

            var tx = (Transaction)Runtime.ScriptContainer;
            var key = new byte[] { Prefix_ContractOwner };
            Storage.Put(Storage.CurrentContext, key, tx.Sender);
        }

        public static void Update(ByteString nefFile, string manifest)
        {
            if (!ValidateContractOwner()) throw new Exception("Only the contract owner can update the contract");

            ContractManagement.Update(nefFile, manifest, null);
        }

        static bool Nep11Transfer(UInt160 scriptHash, UInt160 to, ByteString tokenId, object data = null)
        {
            return (bool)Contract.Call(scriptHash, "transfer", CallFlags.All, to, tokenId, data);
        }

        static bool ValidateContractOwner()
        {
            var key = new byte[] { Prefix_ContractOwner };
            var contractOwner = (UInt160)Storage.Get(Storage.CurrentContext, key);
            var tx = (Transaction)Runtime.ScriptContainer;
            return contractOwner.Equals(tx.Sender) && Runtime.CheckWitness(contractOwner);
        }
    }
}
