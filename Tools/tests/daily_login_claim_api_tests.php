<?php
// Runs the actual endpoint through an owned PHP loopback HTTP fixture, with only
// isolated temporary claim roots. No AO engine, database, or client is contacted.
$endpoint = realpath(__DIR__ . '/../../tools-temp/icc-rk-local-web/daily/claim.php');
if ($endpoint === false) throw new RuntimeException('Daily reward endpoint was not found.');
$temporaryRoot = sys_get_temp_dir() . DIRECTORY_SEPARATOR . 'aorebirth-daily-api-' . bin2hex(random_bytes(12));
if (!mkdir($temporaryRoot, 0700)) throw new RuntimeException('Could not create isolated test data.');
$roots = array($temporaryRoot . DIRECTORY_SEPARATOR . 'one', $temporaryRoot . DIRECTORY_SEPARATOR . 'two');
foreach ($roots as $root) mkdir($root, 0700);
$fixtureAccount = 'fixture-' . bin2hex(random_bytes(12));
$fixtureCharacter = 2147483001;
foreach ($roots as $root) file_put_contents($root . DIRECTORY_SEPARATOR . 'board-character-' . $fixtureCharacter . '.json', json_encode(array('characterId' => $fixtureCharacter, 'accountKey' => $fixtureAccount, 'taken' => array(), 'lastGrantedUtc' => '', 'lastClaimUtc' => '')));

function check($condition, $message) {
    if (!$condition) throw new RuntimeException($message);
}

function invoke_endpoint($method, $query = array(), $body = array(), $selectedRoots = null, $headers = array()) {
    global $endpoint, $roots, $fixtureAccount, $fixtureCharacter;
    $payload = $method === 'POST' ? json_encode($body) : '';
    $claimsRoots = implode(PATH_SEPARATOR, $selectedRoots === null ? $roots : $selectedRoots);
    $reservation = stream_socket_server('tcp://127.0.0.1:0', $socketError, $socketMessage);
    check($reservation !== false, 'Could not reserve an isolated loopback fixture address.');
    $address = stream_socket_get_name($reservation, false);
    fclose($reservation);
    $pipes = array();
    $previousRoots = getenv('AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS');
    putenv('AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS=' . $claimsRoots);
    try {
        $process = proc_open(array(PHP_BINARY, '-n', '-S', $address, '-t', dirname($endpoint)), array(0 => array('pipe', 'r'), 1 => array('pipe', 'w'), 2 => array('pipe', 'w')), $pipes, dirname($endpoint), null);
    } finally {
        putenv($previousRoots === false ? 'AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS' : 'AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS=' . $previousRoots);
    }
    if (!is_resource($process)) throw new RuntimeException('Could not run the owned PHP HTTP fixture.');
    fclose($pipes[0]);
    $response = false;
    $failure = '';
    try {
        $deadline = microtime(true) + 5;
        do {
            $connection = @stream_socket_client('tcp://' . $address, $socketError, $socketMessage, 0.1);
            if ($connection !== false) {
                fclose($connection);
                break;
            }
            $status = proc_get_status($process);
            check($status['running'], 'Owned PHP fixture exited before readiness.');
            usleep(20000);
        } while (microtime(true) < $deadline);
        check($connection !== false, 'Owned PHP fixture exceeded its five-second readiness timeout.');
        if ($method === 'POST') {
            // Prove the configured temporary root reached the real endpoint
            // before any POST can write. Never fall back to actual claim data.
            $proof = @file_get_contents('http://' . $address . '/claim.php?characterId=' . $fixtureCharacter);
            $proof = $proof === false ? null : json_decode($proof, true);
            check(is_array($proof) && isset($proof['accountKey'], $proof['characterId']) && $proof['accountKey'] === $fixtureAccount && $proof['characterId'] === $fixtureCharacter, 'HTTP fixture did not select the isolated claim roots; POST was not sent.');
        }
        $context = stream_context_create(array('http' => array(
            'method' => $method, 'header' => "Content-Type: application/json\r\nConnection: close\r\n" . implode("\r\n", $headers) . "\r\n",
            'content' => $payload, 'ignore_errors' => true, 'timeout' => 5
        )));
        $response = @file_get_contents('http://' . $address . '/claim.php?' . http_build_query($query), false, $context);
    } catch (Throwable $error) {
        $failure = $error->getMessage();
    } finally {
        $status = proc_get_status($process);
        if ($status['running']) proc_terminate($process);
        $stdout = stream_get_contents($pipes[1]);
        $stderr = stream_get_contents($pipes[2]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        proc_close($process);
    }
    check($failure === '', $failure . ' STDERR: ' . substr($stderr, -2000));
    $result = $response === false ? null : json_decode($response, true);
    check(is_array($result), 'Endpoint returned invalid JSON (' . $method . '). RESPONSE: ' . strval($response) . ' STDERR: ' . substr($stderr, -2000));
    return $result;
}

function save_fixture($root, $name, $value) {
    check(file_put_contents($root . DIRECTORY_SEPARATOR . $name, json_encode($value)) !== false, 'Could not write a test fixture.');
}

function test_case($name, $run) {
    $run();
    echo 'PASS ' . $name . PHP_EOL;
}

test_case('GET without hints does not create directories or attribute the globally last-active account', function () {
    global $roots, $temporaryRoot;
    save_fixture($roots[0], 'last-active-account.json', array('accountKey' => 'unrelated'));
    $absent = $temporaryRoot . DIRECTORY_SEPARATOR . 'absent';
    $result = invoke_endpoint('GET', array(), array(), array($absent));
    check($result['hasIdentity'] === false && $result['taken'] === array(), 'Anonymous GET inherited an account.');
    check(!is_dir($absent), 'GET created a claim directory.');
});

test_case('Native character headers bootstrap only the matching character and queue no grant', function () {
    global $roots, $fixtureAccount;
    $headers = array('X-Anarchy-CharacterID: 606');
    $board = invoke_endpoint('GET', array('accountKey' => $fixtureAccount), array(), null, $headers);
    check($board['characterId'] === 606 && $board['nativeCharacterHint'] === true && $board['hasIdentity'] === true, 'Native character routing was not returned.');
    check($board['accountKey'] === '' && $board['taken'] === array(), 'An unknown character inherited an unrelated account board.');
    $queued = invoke_endpoint('POST', array(), array('claimToken' => 'ctest-native-header'), null, $headers);
    check($queued['armed'] === true && $queued['committed'] === false && $queued['characterId'] === 606, 'Native hint did not queue its exact character.');
    foreach ($roots as $root) {
        $request = json_decode(file_get_contents($root . DIRECTORY_SEPARATOR . 'request-ctest-native-header.json'), true);
        check($request['CharacterId'] === 606 && $request['AccountKey'] === '', 'Native hint became an account identity.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'result-ctest-native-header.json'), 'HTTP produced a grant receipt.');
    }
});

test_case('Native headers reject invalid and conflicting routes without changing saved requests', function () {
    global $roots;
    foreach (array('0', '-1', '2147483648', '7x', '7,8') as $invalid) {
        $response = invoke_endpoint('GET', array(), array(), null, array('X-Anarchy-CharacterID: ' . $invalid));
        check($response['ok'] === false, 'Invalid native character header was accepted: ' . $invalid);
    }
    $maximum = invoke_endpoint('GET', array(), array(), null, array('X-Anarchy-CharacterID: 2147483647'));
    check($maximum['characterId'] === 2147483647, 'The native signed character-ID boundary was rejected.');
    $before = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-native-header.json');
    $conflict = invoke_endpoint('POST', array(), array('claimToken' => 'ctest-native-header', 'characterId' => 607), null, array('X-Anarchy-CharacterID: 606'));
    check($conflict['ok'] === false && $conflict['armed'] === false, 'Conflicting body/header character routes were accepted.');
    check(invoke_endpoint('GET', array('characterId' => 607), array(), null, array('X-Anarchy-CharacterID: 606'))['ok'] === false, 'Conflicting query/header routes were accepted.');
    check(invoke_endpoint('POST', array('characterId' => 607), array('claimToken' => 'ctest-native-header', 'characterId' => 606), null, array('X-Anarchy-CharacterID: 606'))['ok'] === false, 'A conflicting query was hidden by the body route.');
    foreach ($roots as $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'request-ctest-native-header.json') === $before, 'A conflict changed the original request.');
});

test_case('POST only queues an immutable token request and never writes progress or a grant result', function () {
    global $roots;
    $body = array('claimToken' => 'ctest-first', 'characterId' => 101, 'accountKey' => 'first');
    $result = invoke_endpoint('POST', array(), $body);
    check(isset($result['armed']) && $result['armed'] === true && $result['committed'] === false, 'POST reported a grant or failed to acknowledge a durable request: ' . json_encode($result));
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-first.json');
    foreach ($roots as $root) {
        check(file_get_contents($root . DIRECTORY_SEPARATOR . 'request-ctest-first.json') === $original, 'Request roots differ.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'account-first.json'), 'HTTP wrote account progress.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'result-ctest-first.json'), 'HTTP wrote a grant result.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'pending-latest.json'), 'HTTP wrote an unsafe global queue.');
    }
    invoke_endpoint('POST', array(), $body);
    check(file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-first.json') === $original, 'Retry changed the original request.');
    $body['characterId'] = 202;
    $conflict = invoke_endpoint('POST', array(), $body);
    check($conflict['ok'] === false, 'Conflicting token ownership was accepted.');
    check(file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-first.json') === $original, 'Conflict discarded the original request.');
});

test_case('Character-owned GET distinguishes missing, pending and committed receipts', function () {
    global $roots;
    $missing = invoke_endpoint('GET', array('token' => 'ctest-absent', 'characterId' => 101));
    check($missing['pending'] === true && $missing['armed'] === false, 'Absent token was reported as armed.');
    $pending = invoke_endpoint('GET', array('token' => 'ctest-first', 'characterId' => 101));
    check($pending['pending'] === true && $pending['armed'] === true && $pending['ok'] === null, 'Queued request was reported as delivered.');
    $receipt = array('ok' => true, 'committed' => true, 'characterId' => 101, 'claimToken' => 'ctest-first', 'accountKey' => 'first', 'taken' => array(1));
    foreach ($roots as $root) save_fixture($root, 'result-character-101-ctest-first.json', $receipt);
    check(invoke_endpoint('GET', array('token' => 'ctest-first', 'characterId' => 101)) === $receipt, 'GET did not return the owned committed result.');
    check(invoke_endpoint('GET', array('token' => 'ctest-first'))['ok'] === false, 'An anonymous result read was attributed to a character.');
});

test_case('Historical account receipts stay immutable even with a character ID until a scoped result exists', function () {
    global $roots;
    $legacy = array('ok' => true, 'committed' => true, 'characterId' => 101, 'day' => 2, 'accountKey' => 'first', 'taken' => array(1, 2));
    foreach ($roots as $root) save_fixture($root, 'result-ctest-legacy.json', $legacy);
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'result-ctest-legacy.json');
    $result = invoke_endpoint('GET', array('token' => 'ctest-legacy', 'characterId' => 101));
    check($result['pending'] === true && $result['legacyVerificationRequired'] === true && !isset($result['taken']), 'Historical account progress became character progress.');
    $owned = array('ok' => true, 'committed' => true, 'characterId' => 101, 'accountKey' => 'first', 'claimToken' => 'ctest-legacy', 'taken' => array(1, 2));
    foreach ($roots as $root) save_fixture($root, 'result-character-101-ctest-legacy.json', $owned);
    check(invoke_endpoint('GET', array('token' => 'ctest-legacy', 'characterId' => 101)) === $owned, 'Owned replay sidecar was not preferred.');
    $sibling = invoke_endpoint('GET', array('token' => 'ctest-legacy', 'characterId' => 102));
    check($sibling['pending'] === true && !isset($sibling['taken']), 'A sibling received another character sidecar.');
    foreach ($roots as $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'result-ctest-legacy.json') === $original, 'Original historical receipt was rewritten.');
});

test_case('Account-only history and old account-copy character boards never supply Taken', function () {
    global $roots;
    $history = array('Taken' => array(1, 2), 'LastGrantedUtc' => gmdate('Y-m-d'));
    $oldBoard = array('accountKey' => 'progress', 'taken' => array(1, 2), 'lastGrantedUtc' => gmdate('Y-m-d'));
    foreach ($roots as $root) { save_fixture($root, 'account-progress.json', $history); save_fixture($root, 'board-character-707.json', $oldBoard); }
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'account-progress.json');
    $old = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'board-character-707.json');
    $result = invoke_endpoint('GET', array('characterId' => 707, 'accountKey' => 'progress'));
    check($result['hasIdentity'] === true && $result['progressVerified'] === false && $result['taken'] === array(), 'Unowned account copy established character progress.');
    check(invoke_endpoint('GET', array('accountKey' => 'progress'))['hasIdentity'] === false, 'An account hint established a character identity.');
    foreach ($roots as $root) { check(file_get_contents($root . DIRECTORY_SEPARATOR . 'account-progress.json') === $original, 'Account history changed.'); check(file_get_contents($root . DIRECTORY_SEPARATOR . 'board-character-707.json') === $old, 'Old character projection changed.'); }
});

test_case('Divergent unattributed account-copy boards do not block native character bootstrap', function () {
    global $roots;
    save_fixture($roots[0], 'board-character-708.json', array('accountKey' => 'old', 'taken' => array(1)));
    save_fixture($roots[1], 'board-character-708.json', array('accountKey' => 'old', 'taken' => array(1, 2)));
    $before = array_map(function ($root) { return file_get_contents($root . DIRECTORY_SEPARATOR . 'board-character-708.json'); }, $roots);
    $result = invoke_endpoint('GET', array(), array(), null, array('X-Anarchy-CharacterID: 708'));
    check($result['hasIdentity'] === true && $result['progressVerified'] === false && $result['taken'] === array(), 'Old account projections blocked native verification or supplied Taken.');
    foreach ($roots as $index => $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'board-character-708.json') === $before[$index], 'An old projection was overwritten.');
});

test_case('Two characters on the same account retain separate daily boards and availability', function () {
    global $roots;
    $first = array('characterId' => 801, 'accountKey' => 'shared', 'taken' => array(1, 2), 'lastGrantedUtc' => gmdate('Y-m-d'), 'lastClaimUtc' => gmdate('Y-m-d'));
    $second = array('characterId' => 802, 'accountKey' => 'shared', 'taken' => array(), 'lastGrantedUtc' => '', 'lastClaimUtc' => '');
    foreach ($roots as $root) { save_fixture($root, 'board-character-801.json', $first); save_fixture($root, 'board-character-802.json', $second); }
    $firstBoard = invoke_endpoint('GET', array('characterId' => 801));
    $secondBoard = invoke_endpoint('GET', array('characterId' => 802));
    check($firstBoard['claimedToday'] === true && $firstBoard['taken'] === array(1, 2), 'First character lost its legitimate progress.');
    check($secondBoard['claimedToday'] === false && $secondBoard['taken'] === array() && $secondBoard['nextDay'] === 1, 'Sibling inherited the account daily lock.');
    check(invoke_endpoint('POST', array(), array('characterId' => 802, 'accountKey' => 'shared', 'claimToken' => 'ctest-sibling'))['armed'] === true, 'Sibling could not queue independently.');
    check(invoke_endpoint('GET', array('characterId' => 801))['taken'] === array(1, 2), 'Sibling queue changed the first character board.');
});

test_case('Cross-character requests, boards and result sidecars fail without rewriting evidence', function () {
    global $roots;
    foreach ($roots as $root) {
        save_fixture($root, 'board-character-803.json', array('characterId' => 804, 'accountKey' => 'shared', 'taken' => array(1)));
        save_fixture($root, 'result-character-803-ctest-wrong-sidecar.json', array('characterId' => 804, 'ok' => true, 'committed' => true, 'taken' => array(1)));
    }
    check(invoke_endpoint('GET', array('characterId' => 803))['ok'] === false, 'Mismatched board owner was accepted.');
    check(invoke_endpoint('GET', array('characterId' => 803, 'token' => 'ctest-wrong-sidecar'))['ok'] === false, 'Mismatched sidecar owner was accepted.');
    check(invoke_endpoint('GET', array('characterId' => 202, 'token' => 'ctest-first'))['ok'] === false, 'Another character receipt was accepted.');
    check(invoke_endpoint('GET', array('characterId' => 202, 'token' => 'ctest-native-header'))['ok'] === false, 'Another character pending request was exposed.');
});

test_case('Scoped receipt token payload must match its filename and requested token', function () {
    global $roots;
    $collision = array('ok' => true, 'committed' => true, 'characterId' => 805, 'accountKey' => 'shared', 'claimToken' => 'character-805-ctest-collision', 'taken' => array(1, 2));
    foreach ($roots as $root) save_fixture($root, 'result-character-805-ctest-collision.json', $collision);
    $before = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'result-character-805-ctest-collision.json');
    $result = invoke_endpoint('GET', array('characterId' => 805, 'token' => 'ctest-collision'));
    check($result['ok'] === false, 'A raw legacy token-name collision was promoted to a new scoped receipt.');
    foreach ($roots as $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'result-character-805-ctest-collision.json') === $before, 'Collision evidence was rewritten.');
});

test_case('Completed character cycles unlock tomorrow UTC through a read-only projection', function () {
    global $roots;
    $yesterday = gmdate('Y-m-d', time() - 86400);
    $board = array('characterId' => 810, 'accountKey' => 'completed', 'taken' => range(1, 28), 'lastGrantedUtc' => $yesterday, 'lastClaimUtc' => $yesterday, 'cycleCompletedOn' => $yesterday, 'claimedToday' => true);
    foreach ($roots as $root) save_fixture($root, 'board-character-810.json', $board);
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'board-character-810.json');
    $result = invoke_endpoint('GET', array('characterId' => 810));
    check($result['taken'] === array() && $result['nextDay'] === 1 && $result['claimedCount'] === 0, 'Completed cycle did not unlock day one.');
    check($result['lastGrantedUtc'] === '' && $result['cycleCompletedOn'] === '' && $result['claimedToday'] === false, 'Completed cycle retained an old daily lock.');
    foreach ($roots as $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'board-character-810.json') === $original, 'Projection rewrote durable progress.');
});

test_case('Incomplete character cycles survive month changes and retain their own UTC daily lock', function () {
    global $roots;
    $board = array('characterId' => 811, 'accountKey' => 'shared-month', 'month' => '2020-01', 'taken' => array(1, 2), 'lastGrantedUtc' => '2020-01-31', 'lastClaimUtc' => '2020-01-31');
    $today = array('characterId' => 812, 'accountKey' => 'shared-month', 'taken' => array(1), 'lastGrantedUtc' => gmdate('Y-m-d'), 'lastClaimUtc' => gmdate('Y-m-d'));
    foreach ($roots as $root) { save_fixture($root, 'board-character-811.json', $board); save_fixture($root, 'board-character-812.json', $today); }
    $result = invoke_endpoint('GET', array('characterId' => 811));
    check($result['taken'] === array(1, 2) && $result['nextDay'] === 3 && $result['month'] === gmdate('Y-m'), 'Month change discarded an incomplete cycle.');
    check($result['claimedToday'] === false, 'A different character daily lock was inherited.');
    check(invoke_endpoint('GET', array('characterId' => 812))['claimedToday'] === true, 'The matching UTC grant lost its daily lock.');
});

test_case('Conflicting character projections fail closed while unrelated account history stays read-only', function () {
    global $roots;
    save_fixture($roots[0], 'board-character-813.json', array('characterId' => 813, 'accountKey' => 'conflict', 'taken' => array(1)));
    save_fixture($roots[1], 'board-character-813.json', array('characterId' => 813, 'accountKey' => 'conflict', 'taken' => array(1, 2)));
    $before = array_map(function ($root) { return file_get_contents($root . DIRECTORY_SEPARATOR . 'board-character-813.json'); }, $roots);
    check(invoke_endpoint('GET', array('characterId' => 813))['ok'] === false, 'Different character mirrors were silently selected.');
    foreach ($roots as $index => $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'board-character-813.json') === $before[$index], 'A conflict overwrote progress.');
});

test_case('Explicit characters can queue but anonymous or account-only requests cannot inherit progress', function () {
    global $roots;
    $board = invoke_endpoint('GET', array('characterId' => 303));
    check($board['hasIdentity'] === true && $board['taken'] === array(), 'A route hint was treated as existing progress.');
    foreach (array(array('claimToken' => 'ctest-unbound'), array('claimToken' => 'ctest-account-only', 'accountKey' => 'shared')) as $request) {
        $result = invoke_endpoint('POST', array(), $request);
        check($result['ok'] === false && $result['armed'] === false, 'A character-less request was queued.');
        foreach ($roots as $root) check(!is_file($root . DIRECTORY_SEPARATOR . 'request-' . $request['claimToken'] . '.json'), 'An ambiguous request was written.');
    }
});

test_case('Owned empty boards stay empty and invalid tokens cannot traverse directories', function () {
    global $roots;
    $board = array('characterId' => 404, 'accountKey' => 'empty', 'taken' => array(), 'lastGrantedUtc' => '', 'lastClaimUtc' => '');
    foreach ($roots as $root) save_fixture($root, 'board-character-404.json', $board);
    $result = invoke_endpoint('GET', array('characterId' => 404, 'accountKey' => 'unrelated'));
    check($result['accountKey'] === 'empty' && $result['taken'] === array(), 'An explicit owner was replaced by a route hint.');
    check(invoke_endpoint('GET', array('characterId' => 404, 'token' => '../account-empty'))['ok'] === false, 'Traversal token was accepted.');
});

test_case('Native token length and portable account metadata remain unchanged for character requests', function () {
    global $roots;
    $tooLong = str_repeat('x', 65);
    check(invoke_endpoint('POST', array(), array('claimToken' => $tooLong, 'characterId' => 505))['ok'] === false, 'An overlong token was queued.');
    $result = invoke_endpoint('POST', array(), array('claimToken' => 'ctest-portable', 'characterId' => 505, 'accountKey' => '  Route:Hint  '));
    check($result['armed'] === true && $result['accountKey'] === 'route_hint', 'Account metadata normalization changed.');
    foreach ($roots as $root) check(!is_file($root . DIRECTORY_SEPARATOR . 'request-' . $tooLong . '.json'), 'An unreadable request was published.');
});

// All files here were created by this test under its exclusive random directory.
$canonical = realpath($temporaryRoot);
check($canonical !== false && strpos($canonical, realpath(sys_get_temp_dir()) . DIRECTORY_SEPARATOR . 'aorebirth-daily-api-') === 0, 'Unexpected cleanup root.');
$iterator = new RecursiveIteratorIterator(new RecursiveDirectoryIterator($canonical, FilesystemIterator::SKIP_DOTS), RecursiveIteratorIterator::CHILD_FIRST);
foreach ($iterator as $entry) {
    $path = $entry->getPathname();
    check(strpos($path, $canonical . DIRECTORY_SEPARATOR) === 0, 'Cleanup path escaped isolated test data.');
    if ($entry->isDir() && !$entry->isLink()) rmdir($path);
    else unlink($path);
}
rmdir($canonical);
