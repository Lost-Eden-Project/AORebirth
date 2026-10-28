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

def reviewed_metadata_exceptions(row, items, flags):
    """Validate only explicitly reviewed flag/target-context false positives."""
    exception=row.get('MetadataExceptions')
    if exception is None:
        return 0, {}
    if (not isinstance(exception,dict)
            or set(exception)-{'AllowedFlags','TargetCriteria','Evidence','Reason'}
            or not isinstance(exception.get('Reason'),str) or not exception['Reason'].strip()
            or not isinstance(exception.get('Evidence'),list) or not exception['Evidence']
            or any(not isinstance(value,str) or not value.strip() for value in exception['Evidence'])):
        raise ValueError('Metadata exceptions require exact evidence and an authored reason')
    names=exception.get('AllowedFlags',[])
    targets=exception.get('TargetCriteria',[])
    if (not isinstance(names,list) or any(not isinstance(name,str) for name in names) or len(names)!=len(set(names))
            or any(name not in {'Special','NoDrop'} for name in names)
            or not isinstance(targets,list) or not names and not targets):
        raise ValueError('Unsupported or empty metadata exception')
    allowed_flags=0
    for name in names:
        if not any(id in items and items[id]['Flags'] & flags[name] for id in row['TemplateIds']):
            raise ValueError('Reviewed flag exception is absent from the exact templates: '+name)
        allowed_flags |= flags[name]
    allowed_terms=defaultdict(set)
    for target in targets:
        if (not isinstance(target,dict) or set(target)!={'StatName','TermIndex','Target'}
                or target['StatName']!='NPCFamily' or target['Target']!='OnTarget'
                or type(target['TermIndex']) is not int or target['TermIndex']<0):
            raise ValueError('Only an exact NPCFamily OnTarget criterion may be excepted')
        found=False
        for id in row['TemplateIds']:
            if id not in items:
                continue
            subject=None
            prior_source=None
            matches=[]
            for position,term in enumerate(items[id]['Criteria']):
                source=term.get('Source')
                if source!=prior_source or term.get('Index')==0:
                    subject=None
                prior_source=source
                operator=term.get('OperatorName')
                if operator in {'OnTarget','OnSelf','OnUser','OnCaster'}:
                    subject=operator
                if term.get('StatName')=='NPCFamily' and term.get('Index')==target['TermIndex']:
                    matches.append((position,subject,operator))
            if matches:
                found=True
                if (len(matches)!=1 or matches[0][1]!='OnTarget'
                        or matches[0][2] not in {'EqualTo','Unequal','GreaterThan','LessThan','BitAnd','NotBitAnd'}):
                    raise ValueError('Reviewed NPCFamily criterion is ambiguous or does not read OnTarget')
                allowed_terms[id].add(matches[0][0])
        if not found:
            raise ValueError('Reviewed target criterion is absent from the exact templates')
    return allowed_flags, allowed_terms


def prepare(input_root: Path, gamedata: Path, output: Path, reviews_path: Path | None=None,
            existing_policy: Path | None=None, existing_review_results: Path | None=None):
    if (existing_policy is None)!=(existing_review_results is None):
        raise ValueError('Additive preparation requires both existing policy and review results')
    source_path=input_root/'item-metadata.jsonl'
    receipt=read(input_root/'item-metadata-source.json')
    if receipt['OutputSha256'] != sha(source_path) or receipt['SourceSha256'] != sha(gamedata/'items.dat'):
        raise ValueError('Item metadata does not match its receipt and canonical items.dat')
    items={}
    with source_path.open(encoding='utf-8') as source:
        for row in map(json.loads,source):
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
    reviews_path=reviews_path or input_root/'ordinary-family-reviews.json'
    reviews=read(reviews_path)
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
    def veto(item, allowed_flags=0, allowed_terms=None):
        if item['Id'] in denied: return 'EXCLUSION_CATALOG'
        if not item['ReadComplete'] or item.get('BytesLeft') != 0: return 'INCOMPLETE_CURRENT_METADATA'
        if item['NanoTemplate']: return 'NANO_EFFECT_NOT_CARRYABLE_CRYSTAL'
        if item['Flags'] & (deny_flags & ~allowed_flags): return 'RESTRICTED_CURRENT_FLAGS'
        if not item['NamedStats'].get('Can',0) & enums['CanFlags']['Carry']: return 'NOT_CARRYABLE'
        if item['Quality'] < 1: return 'INVALID_STORED_QUALITY'
        if item['NamedStats'].get('Rarity',1) not in range(1,6): return 'UNCONFIGURED_RARITY'
        if any(term.get('StatName') in restricted and position not in (allowed_terms or set())
               for position,term in enumerate(item['Criteria'])): return 'RESTRICTED_REQUIREMENT'
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
        allowed_flags,allowed_terms=reviewed_metadata_exceptions(row,items,flags)
        reasons={str(id): ('MISSING_TEMPLATE' if id not in items else veto(items[id],allowed_flags,allowed_terms.get(id))) for id in ids}
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
                proof=list(dict.fromkeys(row['RkEvidence']+row['OrdinaryEvidence']
                    +row.get('MetadataExceptions',{}).get('Evidence',[])))
                entries[interval]={'LowId':low,'HighId':high,'MinQuality':start,'MaxQuality':end,
                    'Classification':'OrdinaryRubiKa','Evidence':proof,'Description':row['Category']+': '+items[low]['Name']}
    values=[entries[key] for key in sorted(entries)]
    retained_definitions=[]
    retained_rejections=[]
    merge_manifest=None
    if existing_policy is not None:
        prior=read(existing_policy)
        prior_reviews=read(existing_review_results)
        prior_summary=prior_reviews['Summary']
        if (prior.get('Version')!=1 or not isinstance(prior.get('Entries'),list)
                or prior_summary.get('AllowlistSha256')!=sha(existing_policy)
                or prior_summary.get('ItemsSha256')!=receipt['SourceSha256']
                or prior_summary.get('FamiliesSha256')!=sha(template_path)):
            raise ValueError('Existing policy/review results are not tied to the current exact sources')
        retained_definitions=prior_reviews['ApprovedDefinitions']
        prior_by_id={definition['Id']:definition for definition in retained_definitions}
        prior_ids={id for entry in prior['Entries'] for id in (entry['LowId'],entry['HighId'])}
        if len(prior_by_id)!=len(retained_definitions) or set(prior_by_id)!=prior_ids:
            raise ValueError('Existing approved definitions do not exactly cover existing policy endpoints')
        for id,row in accepted.items():
            if id in prior_by_id and prior_by_id[id]['Category']!=row['Category']:
                raise ValueError('New review conflicts with the retained ordinary category for '+str(id))
        covered=defaultdict(set)
        for entry in prior['Entries']:
            if (entry.get('Classification')!='OrdinaryRubiKa' or entry['MinQuality']<1
                    or entry['MaxQuality']<entry['MinQuality']):
                raise ValueError('Invalid existing approval entry')
            if entry['LowId'] in denied or entry['HighId'] in denied:
                raise ValueError('Existing approval conflicts with a current exclusion catalog')
            covered[(entry['LowId'],entry['HighId'])].update(range(entry['MinQuality'],entry['MaxQuality']+1))
        additions=[]
        for entry in values:
            key=(entry['LowId'],entry['HighId'])
            uncovered=[quality for quality in range(entry['MinQuality'],entry['MaxQuality']+1)
                       if quality not in covered[key]]
            intervals=[]
            for quality in uncovered:
                if intervals and quality==intervals[-1][1]+1:
                    intervals[-1][1]=quality
                else:
                    intervals.append([quality,quality])
            for minimum,maximum in intervals:
                additions.append({**entry,'MinQuality':minimum,'MaxQuality':maximum})
                covered[key].update(range(minimum,maximum+1))
        # Preserve every previous JSON entry value and its order; append only new pair/QL coverage.
        values=prior['Entries']+additions
        retained_rejections=prior_reviews.get('Rejected',[])
        merge_manifest={'ExistingPolicySha256':sha(existing_policy),'ExistingReviewResultsSha256':sha(existing_review_results),
            'RetainedExistingBands':len(prior['Entries']),'RetainedExistingDefinitions':len(retained_definitions),
            'AddedBands':len(additions),'ExistingEntriesPreservedVerbatim':values[:len(prior['Entries'])]==prior['Entries'],
            'RemovedBands':0,'ChangedExistingDefinitionCategories':0}
    active_ids={value for row in values for value in [row['LowId'],row['HighId']]}
    retained_ids={definition['Id'] for definition in retained_definitions}
    approved_definitions=retained_definitions+[
        {'Id':id,'Name':items[id]['Name'],'StoredQuality':items[id]['Quality'],'Category':accepted[id]['Category'],
         'RuleId':accepted[id]['RuleId'],'FamilyHash':accepted[id].get('FamilyHash')}
        for id in sorted(active_ids-retained_ids)]
    categories_by_id={definition['Id']:definition['Category'] for definition in approved_definitions}
    if merge_manifest is not None:
        merge_manifest.update({'AddedDefinitions':len(active_ids-retained_ids),'ApprovedBands':len(values),
            'ApprovedDefinitions':len(approved_definitions),
            'ExistingApprovedDefinitionsPreservedVerbatim':approved_definitions[:len(retained_definitions)]==retained_definitions})
    coverage=[]
    for ql in range(1,251):
        pool=[entry for entry in values if entry['MinQuality']<=min(250,ql+5) and entry['MaxQuality']>=max(1,ql-5)]
        categories=Counter(categories_by_id[entry['LowId']] for entry in pool)
        coverage.append({'MissionQL':ql,'EligibleBands':len(pool),'Categories':dict(sorted(categories.items()))})
    summary={'Status':'OFFLINE_REVIEW_CANDIDATE_NOT_RUNTIME_VALIDATION','ApprovedDefinitions':len(active_ids),'ApprovedBands':len(values),
        'CategoryDefinitionCounts':dict(sorted(Counter(categories_by_id[id] for id in active_ids).items())),
        'RejectedReviewedFamilies':len(retained_rejections)+len(rejected),'DeniedIdentityCount':len(denied),
        'UncoveredMissionQLs':[row['MissionQL'] for row in coverage if not row['EligibleBands']],
        'ItemMetadataSha256':sha(source_path),'ItemsSha256':receipt['SourceSha256'],'FamiliesSha256':sha(template_path),
        'ReviewsSha256':sha(reviews_path),'Exclusions':exclusion_receipts}
    if merge_manifest is not None:
        summary['AdditiveMerge']=merge_manifest
    output.mkdir(parents=True,exist_ok=True)
    write(output/'MissionChestRKAllowlist.json',{'Version':1,'Entries':values})
    summary['AllowlistSha256']=sha(output/'MissionChestRKAllowlist.json')
    write(output/'ordinary-rk-coverage.json',{'Summary':summary,'MissionCoverage':coverage})
    review_results={'Summary':summary,'Rejected':retained_rejections+rejected,'ApprovedDefinitions':approved_definitions}
    exceptions=[{'RuleId':row['RuleId'],'TemplateIds':row['TemplateIds'],'MetadataExceptions':row['MetadataExceptions']}
                for row in accepted_reviews if row.get('MetadataExceptions')]
    if exceptions:
        review_results['ReviewedMetadataExceptions']=exceptions
    write(output/'ordinary-rk-review-results.json',review_results)
    if merge_manifest is not None:
        write(output/'ordinary-rk-merge-manifest.json',merge_manifest)
    print(json.dumps(summary,ensure_ascii=True))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input-root',type=Path,required=True)
    parser.add_argument('--gamedata-root',type=Path,required=True)
    parser.add_argument('--output-root',type=Path,required=True)
    parser.add_argument('--reviews',type=Path,help='Explicit private review file; reuse the input metadata cache')
    parser.add_argument('--existing-policy',type=Path,help='Retain all existing entries and add uncovered pair/QL intervals')
    parser.add_argument('--existing-review-results',type=Path,help='Source-bound review results for the retained policy')
    args=parser.parse_args()
    prepare(args.input_root,args.gamedata_root,args.output_root,args.reviews,args.existing_policy,args.existing_review_results)
