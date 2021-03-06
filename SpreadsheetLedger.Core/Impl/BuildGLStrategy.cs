﻿using SpreadsheetLedger.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SpreadsheetLedger.Core.Impl
{
    public sealed class BuildGLStrategy : IBuildGLStrategy
    {
        private Dictionary<string, AccountRecord> _coa;
        private ICurrencyConverter _converter;
        private List<JournalRecord> _journal;
        private List<GLRecord> _result;

        private int _journalNextIndex;
        private Dictionary<(string account, string comm), (string comm, decimal amount, decimal amountbc)> _runningBalance;

        public IList<GLRecord> Build(
            IEnumerable<AccountRecord> accountRecords,
            IEnumerable<JournalRecord> journalRecords,
            IEnumerable<CurrencyRecord> currencyRecords,
            IEnumerable<PriceRecord> priceRecords)
        {
            Trace.Assert(_coa == null);
            Trace.Assert(_converter == null);
            Trace.Assert(_journal == null);
            Trace.Assert(_result == null);
            Trace.Assert(_runningBalance == null);

            Trace.Assert(accountRecords != null);
            Trace.Assert(journalRecords != null);
            Trace.Assert(currencyRecords != null);
            Trace.Assert(priceRecords != null);

            try
            {

                _coa = accountRecords
                    .Where(a => !string.IsNullOrEmpty(a.AccountId))
                    .ToDictionary(a => a.AccountId);

                _converter = new CurrencyConverter(currencyRecords, priceRecords);

                _journal = journalRecords
                    .Where(j => j.Date.HasValue)
                    .OrderBy(j => j.Date.Value)
                    .ThenBy(j => j.ToBalance.HasValue)
                    .ToList();

                _result = new List<GLRecord>();
                if (_journal.Count > 0)
                {
                    _journalNextIndex = 0;
                    _runningBalance = new Dictionary<(string account, string comm), (string comm, decimal amount, decimal amountbc)>();

                    var minDate = _journal[0].Date.Value;
                    var maxDate = _journal[_journal.Count - 1].Date.Value;
                    if (maxDate < DateTime.Today)
                        maxDate = DateTime.Today;
                    maxDate = maxDate.AddMonths(1);
                    maxDate = new DateTime(maxDate.Year, maxDate.Month, 1);

                    for (var dt = minDate; dt <= maxDate; dt = dt.AddDays(1))
                    {
                        AppendJournalRecords(dt);
                        AppendRevaluations(dt);
                    }
                }
                return _result;
            }
            finally
            {
                _coa = null;
                _converter = null;
                _journal = null;
                _result = null;
                _journalNextIndex = 0;
                _runningBalance = null;
            }
        }



        private void AppendJournalRecords(DateTime dt)
        {
            var toBalanceSection = false;
            while (true)
            {
                if (_journalNextIndex >= _journal.Count) return;
                var j = _journal[_journalNextIndex];
                Trace.Assert(j.Date.HasValue);
                Trace.Assert(j.Date >= dt);
                if (j.Date > dt) return;
                Trace.Assert(j.Date == dt);
                _journalNextIndex++;

                try
                {
                    // Calculate prices

                    decimal amount;
                    if (j.ToBalance != null)
                    {
                        if (j.Amount != null)
                            throw new LedgerException("Amount and To Balance is specified. Leave only one value.");
                        amount = j.ToBalance.Value - UpdateBalanceTable(j.AccountId, j.Commodity, 0, 0).amount;
                        toBalanceSection = true;
                    }
                    else
                    {
                        if (j.Amount == null) continue;
                        amount = j.Amount.Value;
                        Trace.Assert(!toBalanceSection);        // "To Balance" records should be at the end during the day
                    }

                    if (amount == 0) continue;
                    var amountbc = _converter.Convert(amount, j.Commodity, dt);

                    // Find accounts

                    if (!_coa.TryGetValue(j.AccountId, out AccountRecord account))
                        throw new LedgerException($"Account '{j.AccountId}' not found.");
                    
                    if (!_coa.TryGetValue(j.OffsetAccountId, out AccountRecord offsetAccount))
                        throw new LedgerException($"Offset account '{j.OffsetAccountId}' not found.");

                    // Validate tag
                                        
                    if (!string.IsNullOrEmpty(account.Tag) && (account.Tag != j.Tag))
                        throw new LedgerException($"'{account.AccountId}' account tag ({account.Tag}) doesn't match to journal record tag ({j.Tag}).");

                    if (!string.IsNullOrEmpty(offsetAccount.Tag) && (offsetAccount.Tag != j.Tag))
                        throw new LedgerException($"'{offsetAccount.AccountId}' offset account tag ({offsetAccount.Tag}) doesn't match to journal record tag ({j.Tag}).");

                    // Add GL record

                    AddGLTransaction(
                        dt, j.R, j.Num, j.Text, amount, j.Commodity, amountbc,
                        account, offsetAccount,
                        j.Tag, j.DocText);
                }
                catch (Exception ex)
                {
                    throw new LedgerException("Journal procesing error: " + j.ToString(), ex);
                }
            }
        }

        private void AppendRevaluations(DateTime dt, string accountId = null)
        {
            // Revaluate all accounts only at the end of month
            if (accountId == null && dt.AddDays(1).Day != 1)
                return;

            var keys = _runningBalance.Keys
                .Where(k => accountId == null || k.account == accountId)
                .ToList();

            foreach (var key in keys)
            {
                try
                {
                    // if (key.comm == _converter.BaseCommodity) continue;

                    var account = _coa[key.account];
                    if (IsEquityAccount(account)) continue;

                    // Calculate correction

                    var balance = _runningBalance[key];
                    var newBalance = _converter.Convert(balance.amount, key.comm, dt);
                    var correction = newBalance - balance.amountbc;
                    if (correction == 0) continue;

                    var text = $"{balance.amount} {key.comm}: {balance.amountbc} => {newBalance}";

                    // Find revaluation account

                    if (!_coa.TryGetValue(account.RevaluationAccountId, out var revaluationAccount))
                        throw new LedgerException($"Revaluation account '{account.RevaluationAccountId}' for '{account.AccountId}' not found.");

                    if (!IsEquityAccount(revaluationAccount))
                        throw new LedgerException($"Revaluation account '{account.RevaluationAccountId}' for '{account.AccountId}' is not of type 'E' (Equity).");

                    // Validate tag

                    if (!string.IsNullOrEmpty(account.Tag) && !string.IsNullOrEmpty(revaluationAccount.Tag) && (account.Tag != revaluationAccount.Tag))
                        throw new LedgerException($"'{account.AccountId}' account tag ({account.Tag}) doesn't match to '{revaluationAccount.AccountId}' revaluation account tag ({revaluationAccount.Tag}).");

                    var tag = account.Tag ?? revaluationAccount.Tag;
                    
                    // Add GL record

                    AddGLTransaction(dt, "r", null, text, null, key.comm, correction, account, revaluationAccount, tag, null);
                }
                catch (Exception ex)
                {
                    throw new LedgerException($"Revaluation error for '{key.account}' {key.comm} on {dt.ToShortDateString()}.", ex);
                }
            }
        }


        private void AddGLTransaction(
            DateTime date, string r, string num, string text, decimal? amount, string comm, decimal amountdc,
            AccountRecord account, AccountRecord offset,
            string tag, string docText)
        {
            if (amount.HasValue && amount.Value != 0)
            {
                if (!IsValidCurrency(account, comm))
                {
                    var accCommodity = GetSingleCommodity(account);
                    var accAmount = _converter.Convert(amount.Value, comm, date, accCommodity);

                    if (string.IsNullOrEmpty(account.SettlementAccountId))
                        throw new LedgerException($"Account '{account.AccountId}' doesn't accept '{comm}'. '{nameof(account.SettlementAccountId)}' is not configured.");

                    if (!_coa.TryGetValue(account.SettlementAccountId, out AccountRecord settlementAccount))
                        throw new LedgerException($"Settlement account '{account.SettlementAccountId}' not found.");
                    
                    if (!IsValidCurrency(settlementAccount, comm))
                        throw new LedgerException($"Settlement account '{settlementAccount.AccountId}' doesn't accept '{comm}'.");

                    if (!IsValidCurrency(settlementAccount, accCommodity))
                        throw new LedgerException($"Settlement account '{settlementAccount.AccountId}' doesn't accept '{accCommodity}'.");


                    AddGLTransaction(date, r, num, text, accAmount, accCommodity, amountdc, account, settlementAccount, tag, docText);
                    AddGLTransaction(date, r, num, text, amount, comm, amountdc, settlementAccount, offset, tag, docText);

                    return;
                }

                if (!IsValidCurrency(offset, comm))
                {
                    var accCommodity = GetSingleCommodity(offset);
                    var accAmount = _converter.Convert(amount.Value, comm, date, accCommodity);

                    if (string.IsNullOrEmpty(offset.SettlementAccountId))
                        throw new LedgerException($"Account '{offset.AccountId}' doesn't accept '{comm}'. '{nameof(offset.SettlementAccountId)}' is not configured.");

                    if (!_coa.TryGetValue(offset.SettlementAccountId, out AccountRecord settlementAccount))
                        throw new LedgerException($"Settlement account '{offset.SettlementAccountId}' not found.");

                    if (!IsValidCurrency(settlementAccount, comm))
                        throw new LedgerException($"Settlement account '{settlementAccount.AccountId}' doesn't accept '{comm}'.");

                    if (!IsValidCurrency(settlementAccount, accCommodity))
                        throw new LedgerException($"Settlement account '{settlementAccount.AccountId}' doesn't accept '{accCommodity}'.");


                    AddGLTransaction(date, r, num, text, amount, comm, amountdc, account, settlementAccount, tag, docText);
                    AddGLTransaction(date, r, num, text, accAmount, accCommodity, amountdc, settlementAccount, offset, tag, docText);

                    return;
                }
            }

            UpdateBalanceTable(account.AccountId, comm, amount ?? 0, amountdc);
            UpdateBalanceTable(offset.AccountId, comm, -amount ?? 0, -amountdc);

            _result.Add(new GLRecord
            {
                Date = date,
                R = r,
                Num = num,
                Text = text,
                Amount = amount,
                Commodity = comm,
                AmountDC = amountdc,
                AccountId = account.AccountId,
                AccountName = account.Name,
                AccountName1 = account.Name1,
                AccountName2 = account.Name2,
                AccountName3 = account.Name3,
                AccountName4 = account.Name4,
                AccountType = account.Type,
                OffsetAccountId = offset.AccountId,
                OffsetAccountName = offset.Name,
                Tag = tag,
                DocText = docText
            });

            _result.Add(new GLRecord
            {
                Date = date,
                R = r,
                Num = num,
                Text = text,
                Amount = -amount,
                Commodity = comm,
                AmountDC = -amountdc,
                AccountId = offset.AccountId,
                AccountName = offset.Name,
                AccountName1 = offset.Name1,
                AccountName2 = offset.Name2,
                AccountName3 = offset.Name3,
                AccountName4 = offset.Name4,
                AccountType = offset.Type,
                OffsetAccountId = account.AccountId,
                OffsetAccountName = account.Name,
                Tag = tag,                
                DocText = docText
            });
        }


        private (string comm, decimal amount, decimal amountbc) UpdateBalanceTable(string accountId, string commodity, decimal amount, decimal amountbc)
        {
            Trace.Assert(!string.IsNullOrWhiteSpace(accountId));
            Trace.Assert(!string.IsNullOrWhiteSpace(commodity));

            var key = (accountId, commodity);
            if (_runningBalance.TryGetValue(key, out var value))
            {
                value = (commodity, value.amount + amount, value.amountbc + amountbc);
                _runningBalance[key] = value;
                return value;
            }
            else
            {
                _runningBalance.Add(key, (commodity, amount, amountbc));
                return (commodity, amount, amountbc);
            }
        }

        private static bool IsEquityAccount(AccountRecord account)
        {
            switch (account.Type)
            {
                case "A":
                case "L":
                    return false;
                case "E":
                    return true;
                default:
                    throw new Exception($"Account '{account.AccountId}' has unsupported type: '{account.Type}'. Supported: 'A' (Assets), 'L' (Liabilities) and 'E' (Equity).");
            }
        }

        private static bool IsValidCurrency(AccountRecord account, string comm)
        {
            if (string.IsNullOrWhiteSpace(account.Commodity))
                return true;
            return $",{account.Commodity},".Replace(" ", "").Contains(comm.Trim());
        }

        private static string GetSingleCommodity(AccountRecord account)
        {
            if (string.IsNullOrWhiteSpace(account.Commodity))
                throw new LedgerException($"Invalid operation.");

            if (account.Commodity.Contains(","))
                throw new LedgerException($"Account settlement require single commodity: '{account.AccountId}'.");

            return account.Commodity.Trim();            
        }
    }
}
