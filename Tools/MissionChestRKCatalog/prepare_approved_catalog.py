"""Validate explicit offline family reviews and emit the runtime RK approval catalog.

This does not infer Rubi-Ka origin, inspect names, alter canonical GameData, or
generate runtime items. Positive review evidence is a separate private input.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def write(path, value):
    partial = path.with_suffix(path.suffix + '.partial')
    partial.write_text(json.dumps(value, indent=2, ensure_ascii=True)+'\n', encoding='utf-8')
    partial.replace(path)

def prepare(input_root: Path, gamedata: Path, output: Path):
    source_path=input_root/'item-metadata.jsonl'
    receipt=read(input_root/'item-metadata-source.json')
    if receipt['OutputSha256'] != sha(source_path) or receipt['SourceSha256'] != sha(gamedata/'items.dat'):
        raise ValueError('Item metadata does not match its receipt and canonical items.dat')
    items={}
    for row in map(json.loads,source_path.open(encoding='utf-8')):
        if row['Id'] in items:
            raise ValueError('Duplicate item identity in metadata')
        items[row['Id']]=row
    if len(items) != receipt['RecordCount']:
        raise ValueError('Metadata count does not match receipt')
    template_path=gamedata/'ItemTemplates.json'
    families={key:row['Templates'] for key,row in read(template_path).items() if row.get('Templates')}
    enums=read(input_root/'metadata-enums.json')
    flags=enums['ItemFlags']
    deny_flags=sum(flags[name] for name in ['Special','Disabled','Stationary','NoDrop','Unique'])
    denied=set()
    exclusion_receipts=[]
    for name in ['MissionChestSeasonalExclusions.json','MissionChestSpecialExclusions.json']:
        path=input_root/name
        catalog=read(path)
        if catalog.get('Version') != 1:
            raise ValueError('Unsupported exclusion schema')
        for row in catalog['Entries']:
            denied.update(row.get('TemplateIds',[]))
            family=row.get('FamilyHash')
            if family:
                if family not in families:
                    raise ValueError('Exclusion references a non-leaf family: '+family)
                denied.update(families[family])
        exclusion_receipts.append({'File':name,'Sha256':sha(path)})
    reviews=read(input_root/'ordinary-family-reviews.json')
    if reviews.get('Version') != 1 or reviews.get('ItemsSha256') != receipt['SourceSha256'] or reviews.get('FamiliesSha256') != sha(template_path):
        raise ValueError('Review inputs are not tied to the current item/family source')
    pair_sources={}
    source_root=Path(__file__).resolve().parents[2]
    for relative,expected_sha in reviews.get('PairSources',{}).items():
        path=(source_root/relative).resolve()
        if not path.is_relative_to(source_root) or sha(path)!=expected_sha:
            raise ValueError('Reviewed pair source is outside checkout or changed: '+relative)
        pair_sources[relative]=read(path)
    restricted={'GmLevel','Expansion','QuestStat','QuestInstance','AccessKey','ApartmentAccessCard','NPCFamily','NpcFamily','MonsterData'}
    def veto(item):
        if item['Id'] in denied: return 'EXCLUSION_CATALOG'
        if not item['ReadComplete'] or item.get('BytesLeft') != 0: return 'INCOMPLETE_CURRENT_METADATA'
        if item['NanoTemplate']: return 'NANO_EFFECT_NOT_CARRYABLE_CRYSTAL'
        if item['Flags'] & deny_flags: return 'RESTRICTED_CURRENT_FLAGS'
        if not item['NamedStats'].get('Can',0) & enums['CanFlags']['Carry']: return 'NOT_CARRYABLE'
        if item['Quality'] < 1: return 'INVALID_STORED_QUALITY'
        if item['NamedStats'].get('Rarity',1) not in range(1,6): return 'UNCONFIGURED_RARITY'
        if any(term.get('StatName') in restricted for term in item['Criteria']): return 'RESTRICTED_REQUIREMENT'
        if any(item['NamedStats'].get(stat,0) for stat in restricted): return 'RESTRICTED_DIRECT_STAT'
        return None
    accepted={}
    accepted_reviews=[]
    rejected=[]
    for row in reviews['Reviews']:
        ids=row['TemplateIds']
        if not ids or len(ids)!=len(set(ids)) or not row.get('RkEvidence') or not row.get('OrdinaryEvidence'):
            raise ValueError('Review lacks exact identities or positive/ordinary evidence')
        family=row.get('FamilyHash')
        if family and set(families.get(family,[])) != set(ids):
            raise ValueError('Reviewed family is not the exact canonical leaf: '+family)
        if not family and len(ids)!=1:
            proof=row.get('PairSource') or {}
            relative=proof.get('Path')
            index=proof.get('RowIndex',-1)
            if relative not in pair_sources or index<0 or index>=len(pair_sources[relative]):
                raise ValueError('Multiple reviewed templates require an exact validated pair source')
            original=pair_sources[relative][index]['Key']
            if ids!=[original['LowId'],original['HighId']] or len(ids)!=2:
                raise ValueError('Review differs from its explicit existing pair source')
            if any(id not in items for id in ids) or [items[id]['Quality'] for id in ids]!=[original['LowQl'],original['HighQl']]:
                raise ValueError('Pair source QLs disagree with current stored endpoint metadata')
        reasons={str(id): ('MISSING_TEMPLATE' if id not in items else veto(items[id])) for id in ids}
        bad={key:value for key,value in reasons.items() if value}
        if bad:
            rejected.append({'RuleId':row['RuleId'],'FamilyHash':family,'TemplateIds':ids,'Reasons':bad})
            continue
        qualities=[items[id]['Quality'] for id in ids]
        if len(ids)>1 and len(set(qualities)) != len(qualities):
            rejected.append({'RuleId':row['RuleId'],'FamilyHash':family,'TemplateIds':ids,'Reasons':{'family':'AMBIGUOUS_EQUAL_QUALITY_VARIANTS'}})
            continue
        accepted_reviews.append(row)
        for id in ids:
            prior=accepted.get(id)
            if prior and prior['Category'] != row['Category']:
                raise ValueError('Conflicting reviewed ordinary category for '+str(id))
            accepted[id]=row
    entries={}
    reviewed_family_sets={}
    for row in accepted_reviews:
        key=row.get('FamilyHash') or 'pair:'+','.join(map(str,row['TemplateIds']))
        reviewed_family_sets[key]=row
    for key,row in sorted(reviewed_family_sets.items()):
        ids=sorted(row['TemplateIds'],key=lambda id:(items[id]['Quality'],id))
        if any(id not in accepted for id in ids): continue
        pairs=list(zip(ids,ids[1:])) if len(ids)>1 else [(ids[0],ids[0])]
        for low,high in pairs:
            ranges=row.get('ApprovedQualityRanges') or [[items[low]['Quality'],items[high]['Quality']]]
            for minimum,maximum in ranges:
                if minimum<1 or maximum<minimum:
                    raise ValueError('Invalid reviewed quality interval')
                start=max(items[low]['Quality'],minimum)
                end=min(items[high]['Quality'],maximum)
                if start>end: continue
                interval=(low,high,start,end)
                proof=list(dict.fromkeys(row['RkEvidence']+row['OrdinaryEvidence']))
                entries[interval]={'LowId':low,'HighId':high,'MinQuality':start,'MaxQuality':end,
                    'Classification':'OrdinaryRubiKa','Evidence':proof,'Description':row['Category']+': '+items[low]['Name']}
    values=[entries[key] for key in sorted(entries)]
    active_ids={value for row in values for value in [row['LowId'],row['HighId']]}
    coverage=[]
    for ql in range(1,251):
        pool=[entry for entry in values if entry['MinQuality']<=min(250,ql+5) and entry['MaxQuality']>=max(1,ql-5)]
        categories=Counter(accepted[entry['LowId']]['Category'] for entry in pool)
        coverage.append({'MissionQL':ql,'EligibleBands':len(pool),'Categories':dict(sorted(categories.items()))})
    summary={'Status':'OFFLINE_REVIEW_CANDIDATE_NOT_RUNTIME_VALIDATION','ApprovedDefinitions':len(active_ids),'ApprovedBands':len(values),
        'CategoryDefinitionCounts':dict(sorted(Counter(accepted[id]['Category'] for id in active_ids).items())),
        'RejectedReviewedFamilies':len(rejected),'DeniedIdentityCount':len(denied),
        'UncoveredMissionQLs':[row['MissionQL'] for row in coverage if not row['EligibleBands']],
        'ItemMetadataSha256':sha(source_path),'ItemsSha256':receipt['SourceSha256'],'FamiliesSha256':sha(template_path),
        'ReviewsSha256':sha(input_root/'ordinary-family-reviews.json'),'Exclusions':exclusion_receipts}
    output.mkdir(parents=True,exist_ok=True)
    write(output/'MissionChestRKAllowlist.json',{'Version':1,'Entries':values})
    summary['AllowlistSha256']=sha(output/'MissionChestRKAllowlist.json')
    write(output/'ordinary-rk-coverage.json',{'Summary':summary,'MissionCoverage':coverage})
    write(output/'ordinary-rk-review-results.json',{'Summary':summary,'Rejected':rejected,'ApprovedDefinitions':[
        {'Id':id,'Name':items[id]['Name'],'StoredQuality':items[id]['Quality'],'Category':accepted[id]['Category'],
         'RuleId':accepted[id]['RuleId'],'FamilyHash':accepted[id].get('FamilyHash')} for id in sorted(active_ids)]})
    print(json.dumps(summary,ensure_ascii=True))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input-root',type=Path,required=True)
    parser.add_argument('--gamedata-root',type=Path,required=True)
    parser.add_argument('--output-root',type=Path,required=True)
    args=parser.parse_args()
    prepare(args.input_root,args.gamedata_root,args.output_root)
