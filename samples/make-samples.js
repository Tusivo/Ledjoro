// Generates small synthetic sample files in QuickBooks interchange formats (all data invented for testing).
const fs = require('node:fs'); const path = require('node:path');
const T = (...c) => c.join('\t');
const iif = [
  T('!ACCNT', 'NAME', 'ACCNTTYPE', 'ACCNUM', 'DESC'),
  T('ACCNT', 'Checking', 'BANK', '1000', 'Main account'),
  T('ACCNT', 'Accounts Receivable', 'AR', '1100', ''),
  T('ACCNT', 'Accounts Payable', 'AP', '2000', ''),
  T('ACCNT', 'Sales', 'INC', '4000', ''),
  T('ACCNT', 'Rent', 'EXP', '6060', ''),
  T('!CUST', 'NAME', 'BADDR1', 'BADDR2', 'EMAIL', 'PHONE1', 'TERMS', 'TAXID'),
  T('CUST', 'Acme Widgets', '1 Main St', 'Springfield', 'ap@acme.example', '603-555-0100', 'Net 30', '12-3456789'),
  T('!VEND', 'NAME', 'EMAIL', 'TERMS', 'VEND1099'),
  T('VEND', 'Landlord LLC', 'rent@landlord.example', 'Net 15', 'Y'),
  T('!TRNS', 'TRNSTYPE', 'DATE', 'ACCNT', 'NAME', 'AMOUNT', 'DOCNUM', 'MEMO'),
  T('!SPL', 'TRNSTYPE', 'DATE', 'ACCNT', 'NAME', 'AMOUNT', 'DOCNUM', 'MEMO'),
  T('!ENDTRNS'),
  T('TRNS', 'INVOICE', '01/15/2026', 'Accounts Receivable', 'Acme Widgets', '1250.00', '1001', 'Widgets'),
  T('SPL', 'INVOICE', '01/15/2026', 'Sales', 'Acme Widgets', '-1250.00', '1001', 'Widgets'),
  T('ENDTRNS'),
  T('TRNS', 'PAYMENT', '02/01/2026', 'Checking', 'Acme Widgets', '500.00', '', 'Part payment'),
  T('SPL', 'PAYMENT', '02/01/2026', 'Accounts Receivable', 'Acme Widgets', '-500.00', '', ''),
  T('ENDTRNS'),
  T('TRNS', 'BILL', '02/03/2026', 'Accounts Payable', 'Landlord LLC', '-900.00', 'R-77', 'February rent'),
  T('SPL', 'BILL', '02/03/2026', 'Rent', 'Landlord LLC', '900.00', 'R-77', 'February rent'),
  T('ENDTRNS'),
].join('\r\n') + '\r\n';
const ofx = `OFXHEADER:100
DATA:OFXSGML
VERSION:102
SECURITY:NONE
ENCODING:USASCII
CHARSET:1252
COMPRESSION:NONE
OLDFILEUID:NONE
NEWFILEUID:NONE

<OFX>
<SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20260210120000<LANGUAGE>ENG<FI><ORG>Example Bank<FID>0001</FI></SONRS></SIGNONMSGSRSV1>
<BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STATUS><CODE>0<SEVERITY>INFO</STATUS>
<STMTRS><CURDEF>USD<BANKACCTFROM><BANKID>123456789<ACCTID>000111222333<ACCTTYPE>CHECKING</BANKACCTFROM>
<BANKTRANLIST><DTSTART>20260201<DTEND>20260210
<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260205120000<TRNAMT>-45.67<FITID>20260205-001<NAME>OFFICE SUPPLY CO<MEMO>Printer paper</STMTTRN>
<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260207<TRNAMT>1,200.00<FITID>20260207-002<NAME>CLIENT DEPOSIT</STMTTRN>
<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260209<TRNAMT>-19.99<FITID>20260209-003<NAME>CLOUD SOFTWARE &amp; HOSTING</STMTTRN>
</BANKTRANLIST>
<LEDGERBAL><BALAMT>9999.00<DTASOF>20260210</LEDGERBAL>
</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
`;
const tb = 'Account,Debit,Credit\r\nChecking,"10,000.00",\r\nAccounts Receivable,2500.00,\r\nAccounts Payable,,1800.00\r\nOwner\'s Equity,,10700.00\r\nTOTAL,"12,500.00","12,500.00"\r\n';
const accounts = 'Account #,Account name,Type,Detail type,Description\r\n7100,Cleaning,Expenses,Janitorial,\r\n1050,Petty Cash,Bank,Cash on hand,\r\n';
const dir = __dirname;
fs.writeFileSync(path.join(dir, 'sample.iif'), iif);
fs.writeFileSync(path.join(dir, 'sample.qbo'), ofx);
fs.writeFileSync(path.join(dir, 'sample-trial-balance.csv'), tb);
fs.writeFileSync(path.join(dir, 'sample-accounts.csv'), accounts);
console.log('samples written');
